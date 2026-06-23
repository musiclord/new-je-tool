/*
  Step 1：匯入資料（多來源匯入精靈）。
  一個 GL/TB 資料集 = 一個批次，可由多個檔案或多個工作表組成（guide §3.1.4）：
  選檔（host.selectFiles）→ 逐檔預覽（import.inspectFile：工作表清單／偵測編碼與分隔符）→
  確認後依序匯入（第一個來源 replace 或 append、其後一律 append）。
  前端零解析：欄名集合比對、編碼偵測、合併語意全部在後端。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  var ENCODING_OPTIONS = ['utf-8', 'big5', 'utf-16'];
  var DELIMITER_OPTIONS = [
    { value: ',', label: '逗號 ,' },
    { value: '\t', label: 'Tab' },
    { value: ';', label: '分號 ;' },
    { value: '|', label: '直線 |' }
  ];

  // 精靈區域狀態：一次只開一個資料集的精靈。
  // pending item = { filePath, fileName, fileType, sheetName, include, encoding, delimiter, columnCount }
  var wizard = { kind: null, mode: null, pending: [] };

  // 重新匯入/加入來源完成的「卡片成功態」一次性旗標。{ kind, mode:'replace'|'append', at:ms } | null
  var justImported = null;
  var justImportedTimer = null;

  function resetWizard() {
    wizard = { kind: null, mode: null, pending: [] };
  }

  Ui.registerWorkflowReset(resetWizard);

  Ui.registerWorkflowReset(function () {
    justImported = null;
    if (justImportedTimer) { clearTimeout(justImportedTimer); justImportedTimer = null; }
  });

  /* ---- 渲染 ----------------------------------------------------------------- */

  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('匯入資料');
      Ui.bindNoProjectPanel(container);
      return;
    }

    var calendar = state.importState.calendar;

    container.innerHTML =
      '<div class="panel">' +
        '<h2 class="panel__title">匯入資料</h2>' +
        '<p class="panel__hint">支援 .xlsx／.csv／.txt；一個資料集可由多個檔案或多個工作表合併組成' +
          '（如 Q1–Q4 季別工作表、逐月 CSV）。檔案由後端串流匯入專案資料庫，前端不解析資料。</p>' +
        datasetCard('gl', 'GL（總帳明細）', state.importState.gl) +
        datasetCard('tb', 'TB（試算表）', state.importState.tb) +
        authorizedPreparerCard(state.importState.authorizedPreparer) +
        calendarCard(calendar) +
        '<div class="panel__actions">' + Ui.MOCK_BUTTON_HTML + '</div>' +
        Ui.stepFooterHtml(state) +
      '</div>';

    bindDatasetCard(container, 'gl', 'GL');
    bindDatasetCard(container, 'tb', 'TB');
    bindAuthorizedPreparerCard(container);
    bindCalendarCard(container);
    Ui.bindMockButton(container, 'import');
    Ui.bindStepFooter(container);
  }

  // 授權編製人員清單：單欄姓名 .xlsx，匯入即整份替換、生效。
  // 解鎖「非授權編製人員」預篩選（C5）；前端零解析，名單比對在後端。
  function authorizedPreparerCard(info) {
    // resume（project.load）只回 { rowCount }，無 fileName；匯入當下 setState 才帶 fileName。
    // 有 fileName 才補上來源檔名後綴，缺檔名仍正確顯示「已匯入」態（前端零邏輯，只讀 state）。
    var body = info
      ? '<p class="import-card__status import-card__status--ok">已匯入 ' + info.rowCount + ' 位編製人員' +
          (info.fileName ? '（' + Ui.esc(info.fileName) + '）' : '') + '。</p>'
      : '<p class="import-card__status">尚未匯入。匯入後可執行「非授權編製人員」預篩選。</p>';

    return (
      '<section class="import-card" data-bind="import-card-authorized-preparer">' +
        '<h3 class="import-card__title">授權編製人員清單（單欄姓名）</h3>' +
        body +
        '<div class="import-card__actions">' +
          '<button type="button" class="btn' + (info ? ' btn--ghost' : '') +
            '" data-action="import-authorized-preparer">' +
            (info ? '重新匯入授權清單' : '選擇授權清單檔') + '</button>' +
          (info
            ? '<button type="button" class="btn btn--ghost" data-action="preview-authorized-preparer"' +
                ' title="開啟資料預覽，檢視已匯入的授權編製人員清單">預覽授權清單</button>'
            : '') +
        '</div>' +
      '</section>'
    );
  }

  function bindAuthorizedPreparerCard(container) {
    // 預覽入口：沿用全域資料預覽面板，看已匯入的授權編製人員清單前 50 筆。
    var apPreviewBtn = container.querySelector('[data-action="preview-authorized-preparer"]');
    if (apPreviewBtn && Ui.openDataPreview) {
      apPreviewBtn.addEventListener('click', function () {
        Ui.openDataPreview('authorizedPreparers');
      });
    }

    var btn = container.querySelector('[data-action="import-authorized-preparer"]');
    if (!btn) { return; }

    btn.addEventListener('click', function () {
      Ui.run('匯入授權編製人員清單', function () {
        return global.JetApi.hostSelectFile({
          title: '授權編製人員清單',
          extensions: ['.xlsx']
        }).then(function (file) {
          if (!file.filePath) { return; }
          return global.JetApi.importAuthorizedPreparerFromFile({
            filePath: file.filePath,
            fileName: file.fileName
          }).then(function (data) {
            Store.setAuthorizedPreparerState({
              batchId: data.batchId,
              rowCount: data.rowCount,
              fileName: data.fileName,
              importedUtc: data.importedUtc
            });
            Store.addMessage('授權編製人員清單匯入完成：' + data.rowCount + ' 位人員。', 'info');
          });
        });
      });
    });
  }

  // 日期維度：上傳事務所假日／補班 .xlsx + 設定每週非工作日（週末判定）。前端零邏輯，權威在後端。
  function calendarCard(calendar) {
    var hasAny = calendar && (calendar.holidayCount || calendar.makeupDayCount);
    var body = hasAny
      ? '<p class="import-card__status import-card__status--ok">假日 ' +
          (calendar.holidayCount || 0) + ' 天、補班 ' + (calendar.makeupDayCount || 0) + ' 天。</p>'
      : '<p class="import-card__status">尚未載入假日／補班日。上傳事務所行事曆檔（.xlsx）即可。</p>';

    return (
      '<section class="import-card" data-bind="import-card-calendar">' +
        '<h3 class="import-card__title">日期維度（假日／補班日）</h3>' +
        body +
        '<div class="import-card__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="import-holiday">上傳假日檔</button>' +
          '<button type="button" class="btn btn--ghost" data-action="import-makeup">上傳補班檔</button>' +
        '</div>' +
        '<div class="calendar-nonworking">' +
          '<span class="calendar-nonworking__label">非工作日（週末判定）</span>' +
          '<div class="calendar-nonworking__days" data-bind="nonworking-days">' +
            weekdayCheckboxesHtml(calendar && calendar.nonWorkingDays) +
          '</div>' +
          '<p class="calendar-nonworking__hint">未調整時預設週六、週日。影響週末過帳／核准規則與週末篩選條件。</p>' +
        '</div>' +
      '</section>'
    );
  }

  // 一週七天的非工作日勾選；canonical .NET DayOfWeek（週日=0…週六=6），UI 以週一→週日排列。
  function weekdayCheckboxesHtml(nonWorkingDays) {
    var set = nonWorkingDays || [0, 6];
    var days = [
      { v: 1, label: '一' }, { v: 2, label: '二' }, { v: 3, label: '三' },
      { v: 4, label: '四' }, { v: 5, label: '五' }, { v: 6, label: '六' }, { v: 0, label: '日' }
    ];
    return days.map(function (d) {
      var checked = set.indexOf(d.v) >= 0 ? ' checked' : '';
      return '<label class="weekday-toggle">' +
        '<input type="checkbox" data-weekday="' + d.v + '"' + checked + '>' +
        '<span>' + d.label + '</span></label>';
    }).join('');
  }

  function bindCalendarCard(container) {
    var holidayBtn = container.querySelector('[data-action="import-holiday"]');
    if (holidayBtn) {
      holidayBtn.addEventListener('click', function () {
        Ui.run('上傳假日檔', function () {
          return global.JetApi.hostSelectFile({
            title: '選擇假日檔（Date_of_Holiday、Holiday_Name、IS_Holiday）',
            extensions: ['.xlsx']
          }).then(function (file) {
            if (!file.filePath) { return; }
            return global.JetApi.importHolidayFromFile({
              filePath: file.filePath,
              fileName: file.fileName
            }).then(function (data) {
              var existing = Store.getState().importState.calendar || {};
              Store.setCalendarState({
                holidayCount: data.count,
                makeupDayCount: existing.makeupDayCount || 0,
                nonWorkingDays: existing.nonWorkingDays
              });
              Store.addMessage('假日匯入完成：' + data.count + ' 天。', 'info');
            });
          });
        });
      });
    }

    var makeupBtn = container.querySelector('[data-action="import-makeup"]');
    if (makeupBtn) {
      makeupBtn.addEventListener('click', function () {
        Ui.run('上傳補班檔', function () {
          return global.JetApi.hostSelectFile({
            title: '選擇補班檔（Date_of_MakeUpday、MakeUpDay_Desc）',
            extensions: ['.xlsx']
          }).then(function (file) {
            if (!file.filePath) { return; }
            return global.JetApi.importMakeupDayFromFile({
              filePath: file.filePath,
              fileName: file.fileName
            }).then(function (data) {
              var existing = Store.getState().importState.calendar || {};
              Store.setCalendarState({
                holidayCount: existing.holidayCount || 0,
                makeupDayCount: data.count,
                nonWorkingDays: existing.nonWorkingDays
              });
              Store.addMessage('補班匯入完成：' + data.count + ' 天。', 'info');
            });
          });
        });
      });
    }

    // 非工作日（週幾）勾選：任一變更即蒐集勾選的 canonical 值送後端，回傳後更新狀態。
    container.querySelectorAll('[data-bind="nonworking-days"] input[data-weekday]').forEach(function (cb) {
      cb.addEventListener('change', function () {
        var days = [];
        container.querySelectorAll('[data-bind="nonworking-days"] input[data-weekday]').forEach(function (b) {
          if (b.checked) { days.push(Number(b.getAttribute('data-weekday'))); }
        });
        Ui.run('設定非工作日', function () {
          return global.JetApi.calendarSetNonWorkingDays({ days: days }).then(function (data) {
            var existing = Store.getState().importState.calendar || {};
            Store.setCalendarState({
              holidayCount: existing.holidayCount || 0,
              makeupDayCount: existing.makeupDayCount || 0,
              nonWorkingDays: data.nonWorkingDays
            });
            Store.addMessage('已更新非工作日設定。', 'info');
          });
        });
      });
    });
  }

  // 卡片三狀態互斥：工作區啟動時不渲染摘要與入口鈕 → 永不三鈕同框。
  function datasetCard(kind, title, info) {
    var body;
    if (wizard.kind === kind) {
      body = wizardWorkspaceHtml(kind, info);
    } else if (info) {
      body = summaryFaceHtml(kind, info);
    } else {
      body = emptyFaceHtml(kind);
    }

    return (
      '<section class="import-card" data-bind="import-card-' + kind + '">' +
        '<h3 class="import-card__title">' + title + '</h3>' +
        body +
      '</section>'
    );
  }

  function summaryFaceHtml(kind, info) {
    var justBadge = '';
    if (justImported && justImported.kind === kind) {
      var label = justImported.mode === 'append' ? '已加入來源' : '剛剛重新匯入';
      justBadge = '<p class="import-card__just" data-bind="just-imported-' + kind + '">' +
        '<span class="import-card__just-mark" aria-hidden="true">✓</span> ' + label + '·' +
        new Date(justImported.at).toLocaleTimeString('zh-Hant', { hour12: false }) + '</p>';
    }

    return (
      justBadge +
      '<p class="import-card__status import-card__status--ok">已匯入 ' + info.rowCount + ' 列、' +
        info.columns.length + ' 欄（' + ((info.sources || []).length || 1) + ' 個來源）。</p>' +
      sourceListHtml(info.sources || []) +
      '<div class="import-card__actions">' +
        '<button type="button" class="btn btn--ghost" data-action="wizard-append-' + kind + '">加入來源</button>' +
        '<button type="button" class="btn btn--ghost" data-action="wizard-replace-' + kind + '">重新匯入</button>' +
      '</div>'
    );
  }

  function emptyFaceHtml(kind) {
    return (
      '<p class="import-card__status">尚未匯入。可由多個檔案或多個工作表合併成一個資料集。</p>' +
      '<div class="import-card__actions">' +
        '<button type="button" class="btn" data-action="wizard-replace-' + kind + '">選擇來源檔</button>' +
      '</div>'
    );
  }

  function sourceListHtml(sources) {
    if (!sources.length) { return ''; }

    var rows = sources.map(function (s) {
      var name = Ui.esc(s.fileName) + (s.sheetName ? '<span class="source-list__sheet">' +
        Ui.esc(s.sheetName) + '</span>' : '');
      var detail = [];
      if (s.encoding) { detail.push('編碼 ' + Ui.esc(s.encoding)); }
      if (s.delimiter) { detail.push('分隔符 ' + Ui.esc(s.delimiter === '\t' ? 'Tab' : s.delimiter)); }

      return (
        '<tr>' +
          '<td class="source-list__no">' + s.sourceNo + '</td>' +
          '<td>' + name + (detail.length ? '<span class="source-list__detail">' + detail.join('・') + '</span>' : '') + '</td>' +
          '<td class="source-list__rows">' + Number(s.rowCount).toLocaleString() + '</td>' +
          '<td class="source-list__time">' + new Date(s.importedUtc).toLocaleString('zh-Hant', { hour12: false }) + '</td>' +
        '</tr>'
      );
    }).join('');

    return (
      '<table class="source-list">' +
        '<thead><tr><th>#</th><th>來源（檔案／工作表）</th><th>列數</th><th>匯入時間</th></tr></thead>' +
        '<tbody>' + rows + '</tbody>' +
      '</table>'
    );
  }

  // 模式橫幅：明講意圖與後果（append / 破壞性 replace / 首次建立）。
  // 匯入進行中改顯示中性「匯入中」：使用者已按下「開始匯入」，「即將取代」的預警已過時，
  // 不該在匯入期間（大檔可能數十秒）續掛破壞性警告，造成「完成後還掛著警告」的錯覺。
  function wizardBannerHtml(info) {
    if (wizard.importing) {
      return '<p class="wizard-pane__hint">匯入中…請稍候，完成後會顯示匯入結果。</p>';
    }
    if (wizard.mode === 'append') {
      return '<p class="wizard-pane__hint">加入來源：新檔的資料會附加到現有 ' +
        Number(info.rowCount).toLocaleString() + ' 列（欄位名稱需與現有一致）。加入後欄位配對要重做，先前的測試與篩選結果也會清除。</p>';
    }
    if (info) {
      return '<p class="wizard-pane__hint wizard-pane__hint--danger">你正在重新匯入這個資料集。開始後，現有的 ' +
        Number(info.rowCount).toLocaleString() + ' 列會被新來源取代，欄位配對要重做，先前的測試與篩選結果也會清除。</p>';
    }
    return '<p class="wizard-pane__hint">建立資料集 — 選擇一個或多個來源檔（可多檔／多工作表合併）。</p>';
  }

  function wizardWorkspaceHtml(kind, info) {
    var hasFiles = wizard.pending.length > 0;
    var includedCount = wizard.pending.filter(function (i) { return i.include; }).length;
    var pickLabel = hasFiles ? '再加入檔案' : '選擇來源檔（可多選）';

    // 匯入進行中：只留橫幅與進度，收掉所有動作鈕（不能在匯入途中改選來源／取消）。
    var actions = wizard.importing ? '' : (
      '<div class="import-card__actions">' +
        '<button type="button" class="btn' + (hasFiles ? ' btn--ghost' : '') +
          '" data-action="wizard-pick">' + pickLabel + '</button>' +
        (hasFiles
          ? '<button type="button" class="btn" data-action="wizard-confirm"' +
              (includedCount === 0 ? ' disabled' : '') +
              '>開始匯入（' + includedCount + ' 個來源）</button>'
          : '') +
        '<button type="button" class="btn btn--ghost" data-action="wizard-cancel">取消</button>' +
      '</div>'
    );

    return (
      '<div class="wizard-pane">' +
        wizardBannerHtml(info) +
        (hasFiles ? pendingListHtml() : '') +
        '<div class="wizard-progress" data-bind="wizard-progress" hidden>' +
          '<p class="wizard-progress__label" data-bind="wizard-progress-label"></p>' +
          '<div class="wizard-progress__track"><div class="wizard-progress__fill" data-bind="wizard-progress-fill"></div></div>' +
        '</div>' +
        actions +
      '</div>'
    );
  }

  function pendingListHtml() {
    var rows = wizard.pending.map(function (item, index) {
      var name = Ui.esc(item.fileName) + (item.sheetName
        ? '<span class="source-list__sheet">' + Ui.esc(item.sheetName) + '</span>' : '');

      return (
        '<li class="pending-row">' +
          '<div class="pending-row__head">' +
            '<label class="pending-row__main">' +
              '<input type="checkbox" data-pending-bind="include" data-index="' + index + '"' +
                (item.include ? ' checked' : '') + '>' +
              '<span>' + name + '</span>' +
            '</label>' +
            pendingDetailHtml(item, index) +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="pending-preview" ' +
              'data-index="' + index + '">' + (item.previewOpen ? '預覽 ▾' : '預覽 ▸') + '</button>' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="pending-remove" ' +
              'data-index="' + index + '" title="從清單移除這個來源">移除</button>' +
          '</div>' +
          (item.previewOpen ? previewTableHtml(item) : '') +
        '</li>'
      );
    }).join('');

    return '<ul class="wizard-pane__list">' + rows + '</ul>';
  }

  function pendingDetailHtml(item, index) {
    if (item.fileType === 'csv') {
      var encodingOptions = ENCODING_OPTIONS.map(function (enc) {
        return '<option value="' + enc + '"' + (item.encoding === enc ? ' selected' : '') + '>' + enc + '</option>';
      }).join('');

      var delimiterOptions = '<option value=""' + (item.delimiter ? '' : ' selected') + '>自動</option>' +
        DELIMITER_OPTIONS.map(function (d) {
          return '<option value="' + (d.value === '\t' ? '&#9;' : d.value) + '"' +
            (item.delimiter === d.value ? ' selected' : '') + '>' + d.label + '</option>';
        }).join('');

      return (
        '<span class="pending-row__detail">' +
          '<label>編碼 <select data-pending-bind="encoding" data-index="' + index + '">' +
            encodingOptions + '</select></label>' +
          '<label>分隔符 <select data-pending-bind="delimiter" data-index="' + index + '">' +
            delimiterOptions + '</select></label>' +
        '</span>'
      );
    }

    var estimate = item.rowCountEstimate != null
      ? '・約 ' + Number(item.rowCountEstimate).toLocaleString() + ' 列' : '';
    return '<span class="pending-row__detail">' + item.columnCount + ' 欄' + estimate + '</span>';
  }

  // 逐來源預覽：表頭 + 前 10 列原貌（import.previewFile）。有界，絕不載入完整母體。
  function previewTableHtml(item) {
    if (!item.previewData) {
      return '<div class="pending-preview"><p class="pending-preview__hint">載入預覽中…</p></div>';
    }

    var data = item.previewData;
    var head = '<tr>' + data.columns.map(function (c) {
      return '<th>' + Ui.esc(c) + '</th>';
    }).join('') + '</tr>';

    var body = data.sampleRows.map(function (row) {
      return '<tr>' + row.map(function (cell) {
        return cell === null ? '<td class="is-null">—</td>' : '<td>' + Ui.esc(cell) + '</td>';
      }).join('') + '</tr>';
    }).join('');

    return (
      '<div class="pending-preview">' +
        '<p class="pending-preview__hint">最上方一列是被當成欄名的標頭；若它看起來是資料而非欄名，' +
          '代表這份檔案可能沒有標頭列。</p>' +
        '<div class="pending-preview__grid">' +
          '<table class="data-preview__table"><thead>' + head + '</thead><tbody>' + body + '</tbody></table>' +
        '</div>' +
      '</div>'
    );
  }

  /* ---- 匯入進度（import.progress 事件，manifest「Host→Web 事件」） ---------------- */

  // 進度是 UX 提示：百分比以 inspect 的 rowCountEstimate 估算（dimension 推估、可能過時），
  // 權威列數以匯入 response 為準。estimate 缺席（CSV）→ 只顯示已寫入列數。
  function updateProgress(item, itemIndex, totalItems, data) {
    var pane = document.querySelector('[data-bind="wizard-progress"]');
    var label = document.querySelector('[data-bind="wizard-progress-label"]');
    var fill = document.querySelector('[data-bind="wizard-progress-fill"]');
    if (!pane || !label || !fill) { return; }

    pane.hidden = false;

    var sourceName = data.fileName + (data.sheetName ? '［' + data.sheetName + '］' : '');
    var countText = Number(data.rowsRead).toLocaleString() + ' 列';
    var position = totalItems > 1 ? '來源 ' + (itemIndex + 1) + '／' + totalItems + '：' : '';

    if (item && item.rowCountEstimate) {
      var percent = Math.min(99, Math.round(data.rowsRead / item.rowCountEstimate * 100));
      label.textContent = position + sourceName + '　已寫入 ' + countText + '（約 ' + percent + '%）';
      fill.style.width = percent + '%';
    } else {
      label.textContent = position + sourceName + '　已寫入 ' + countText;
      fill.style.width = '100%';
      fill.classList.add('wizard-progress__fill--indeterminate');
    }
  }

  /* ---- 事件 ------------------------------------------------------------------ */

  function bindDatasetCard(container, kind, label) {
    var card = container.querySelector('[data-bind="import-card-' + kind + '"]');
    if (!card) { return; }

    // 卡片成功態:約 4 秒後淡出並收回常態(一次性,避免每次重繪堆疊計時器)。
    if (justImported && justImported.kind === kind && !justImportedTimer) {
      justImportedTimer = setTimeout(function () {
        var badge = document.querySelector('[data-bind="just-imported-' + kind + '"]');
        if (badge) { badge.classList.add('is-fading'); }
        setTimeout(function () {
          justImported = null;
          justImportedTimer = null;
          Store.touch();
        }, 400);
      }, 4000);
    }

    // 摘要面 / 空狀態入口（工作區未啟動時才存在）
    var appendBtn = card.querySelector('[data-action="wizard-append-' + kind + '"]');
    if (appendBtn) {
      appendBtn.addEventListener('click', function () { openWorkspace(kind, label, 'append'); });
    }

    var replaceBtn = card.querySelector('[data-action="wizard-replace-' + kind + '"]');
    if (replaceBtn) {
      replaceBtn.addEventListener('click', function () { openWorkspace(kind, label, 'replace'); });
    }

    if (wizard.kind !== kind) { return; }

    // 工作區
    var pickBtn = card.querySelector('[data-action="wizard-pick"]');
    if (pickBtn) {
      pickBtn.addEventListener('click', function () { pickSources(kind, label); });
    }

    var confirmBtn = card.querySelector('[data-action="wizard-confirm"]');
    if (confirmBtn) {
      confirmBtn.addEventListener('click', function () { confirmWizard(kind, label); });
    }

    var cancelBtn = card.querySelector('[data-action="wizard-cancel"]');
    if (cancelBtn) {
      cancelBtn.addEventListener('click', function () { resetWizard(); Store.touch(); });
    }

    card.querySelectorAll('[data-action="pending-preview"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        togglePreview(kind, label, Number(btn.getAttribute('data-index')));
      });
    });

    // 單列移除：誤加的來源可直接從待匯入清單拿掉，不必整個精靈取消。splice 後重繪即重新編號。
    card.querySelectorAll('[data-action="pending-remove"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        wizard.pending.splice(Number(btn.getAttribute('data-index')), 1);
        Store.touch();
      });
    });

    card.querySelectorAll('[data-pending-bind]').forEach(function (control) {
      var index = Number(control.getAttribute('data-index'));
      var key = control.getAttribute('data-pending-bind');

      control.addEventListener('change', function () {
        if (key === 'include') {
          wizard.pending[index].include = control.checked;
          Store.touch(); // 重建以更新「開始匯入（n）」計數
          return;
        }
        wizard.pending[index][key] = control.value || null;
        wizard.pending[index].previewData = null; // 編碼/分隔變更 → 預覽快取失效
        if (wizard.pending[index].previewOpen) {
          wizard.pending[index].previewOpen = false;
          togglePreview(kind, label, index); // 重抓
        } else {
          Store.touch();
        }
      });
    });
  }

  // 入口：開啟工作區（設定 kind/mode、清空待匯入清單）。
  // 首次建立（空資料集、無破壞性疑慮）直接開檔，少一次點擊；
  // 已有資料時不自動開檔——讓使用者先看到模式橫幅、可在開檔前反悔（尤其破壞性的重新匯入）。
  function openWorkspace(kind, label, mode) {
    var hasData = !!Store.getState().importState[kind];
    wizard = { kind: kind, mode: mode, pending: [] };
    Store.touch();
    if (mode === 'replace' && !hasData) {
      pickSources(kind, label);
    }
  }

  // 選檔 → 逐檔 inspect → 附加到工作區待匯入清單（xlsx 每個非空工作表一個來源）。
  function pickSources(kind, label) {
    Ui.run('選擇來源檔', function () {
      return global.JetApi.hostSelectFiles({
        title: '選擇 ' + label + ' 來源檔（可多選）',
        extensions: ['.xlsx', '.csv', '.txt']
      }).then(function (data) {
        var files = data.files || [];
        if (files.length === 0) { return; }

        return files.reduce(function (chain, file) {
          return chain.then(function () {
            return global.JetApi.importInspectFile({ filePath: file.filePath }).then(function (info) {
              if (info.fileType === 'xlsx') {
                (info.worksheets || []).forEach(function (ws) {
                  if (ws.columns.length === 0) { return; } // 空工作表不列入
                  wizard.pending.push({
                    filePath: file.filePath,
                    fileName: file.fileName,
                    fileType: 'xlsx',
                    sheetName: ws.name,
                    include: true,
                    encoding: null,
                    delimiter: null,
                    columnCount: ws.columns.length,
                    rowCountEstimate: ws.rowCountEstimate != null ? ws.rowCountEstimate : null,
                    previewOpen: false,
                    previewData: null
                  });
                });
                return;
              }

              wizard.pending.push({
                filePath: file.filePath,
                fileName: file.fileName,
                fileType: 'csv',
                sheetName: null,
                include: true,
                encoding: info.encoding,
                delimiter: info.delimiter,
                columnCount: (info.columns || []).length,
                previewOpen: false,
                previewData: null
              });
            });
          });
        }, Promise.resolve()).then(function () {
          if (wizard.pending.length === 0) {
            Store.addMessage('選取的檔案沒有可匯入的內容（工作表皆為空）。', 'warn');
          }
          Store.touch();
        });
      });
    });
  }

  // 展開/收合單一來源的「表頭 + 前 10 列」預覽；有界（limit 10），絕不載入完整母體。
  function togglePreview(kind, label, index) {
    var item = wizard.pending[index];
    if (!item) { return; }

    if (item.previewOpen) {
      item.previewOpen = false;
      Store.touch();
      return;
    }

    item.previewOpen = true;
    if (item.previewData) { Store.touch(); return; } // 已快取，直接顯示

    Store.touch(); // 先顯示「載入預覽中…」

    Ui.run('載入預覽', function () {
      var payload = { filePath: item.filePath, limit: 10 };
      if (item.sheetName) { payload.sheetName = item.sheetName; }
      if (item.encoding) { payload.encoding = item.encoding; }
      if (item.delimiter) { payload.delimiter = item.delimiter; }

      return global.JetApi.importPreviewFile(payload).then(function (data) {
        item.previewData = { columns: data.columns || [], sampleRows: data.sampleRows || [] };
        Store.touch();
      }).catch(function (error) {
        item.previewOpen = false; // 失敗則收合，避免卡在「載入中」
        item.previewData = null;
        Store.touch();
        throw error; // 交給 run() 顯示錯誤訊息
      });
    });
  }

  // 依序匯入勾選的來源：第一個來源用精靈模式（replace / append）、其後一律 append。
  // 失敗即中止：已成功的來源保留（最後一次成功 response 反映到狀態），其餘留在清單供修正。
  function confirmWizard(kind, label) {
    var items = wizard.pending.filter(function (i) { return i.include; });
    if (items.length === 0) { return; }

    var invoke = kind === 'gl' ? global.JetApi.importGlFromFile : global.JetApi.importTbFromFile;
    var lastResponse = null;
    var importedCount = 0;
    var currentIndex = 0;

    // 匯入期間訂閱 import.progress；結束（成功或失敗）一律解除
    var onProgress = function (data) {
      if (data.kind !== kind) { return; }
      updateProgress(items[currentIndex], currentIndex, items.length, data);
    };
    global.JetApi.on('import.progress', onProgress);
    var unsubscribe = function () { global.JetApi.off('import.progress', onProgress); };

    var startedAt = Date.now();

    // 進入匯入態：Ui.run 內 setBusy(true) 會立即觸發重繪，橫幅換成「匯入中」、動作鈕收起。
    wizard.importing = true;

    Ui.run('匯入 ' + label, function () {
      return items.reduce(function (chain, item, index) {
        return chain.then(function () {
          currentIndex = index;
          var payload = {
            filePath: item.filePath,
            fileName: item.fileName,
            mode: index === 0 ? wizard.mode : 'append'
          };
          if (item.sheetName) { payload.sheetName = item.sheetName; }
          if (item.encoding) { payload.encoding = item.encoding; }
          if (item.delimiter) { payload.delimiter = item.delimiter; }

          return invoke(payload).then(function (data) {
            lastResponse = data;
            importedCount++;
          });
        });
      }, Promise.resolve()).then(function () {
        unsubscribe();
        justImported = { kind: kind, mode: wizard.mode, at: Date.now() };
        applyResponse(kind, lastResponse);
        resetWizard();
        Store.addMessage(
          label + ' 匯入完成：' + importedCount + ' 個來源、共 ' +
          Number(lastResponse.rowCount).toLocaleString() + ' 列、' +
          lastResponse.columns.length + ' 欄，耗時 ' +
          ((Date.now() - startedAt) / 1000).toFixed(1) + ' 秒。', 'info');
      }).catch(function (error) {
        unsubscribe();
        wizard.importing = false; // 退出匯入態，橫幅恢復預警、動作鈕回來供修正後重試
        // 部分成功：反映已落地的批次狀態，剩餘來源留在精靈清單
        if (lastResponse) {
          applyResponse(kind, lastResponse);
          wizard.pending = wizard.pending.filter(function (i) {
            return !i.include || items.indexOf(i) >= importedCount;
          });
          Store.touch();
        }
        throw error; // 交給 run() 顯示錯誤訊息（如 column_mismatch 的差集說明）
      });
    });
  }

  function applyResponse(kind, data) {
    var sources = data.sources || [];
    Store.setImportResult(kind, {
      batchId: data.batchId,
      rowCount: data.rowCount,
      columns: data.columns,
      fileName: sources.length ? sources[0].fileName : '',
      sources: sources
    });
  }

  /* ---- MockDataLoader -------------------------------------------------------- */

  function mockImportData() {
    if (!Store.getState().project) {
      Store.addMessage('請先建立或載入專案，再套用測試匯入。', 'warn');
      return;
    }

    Ui.run('套用測試案件（匯入）', function () {
      var demo;
      return global.JetApi.projectLoadDemo({}).then(function (d) {
        demo = d;
        return global.JetApi.demoExportGlFile({});
      }).then(function (glFile) {
        return global.JetApi.importGlFromFile({
          filePath: glFile.filePath,
          fileName: glFile.fileName
        });
      }).then(function (glImport) {
        applyResponse('gl', glImport);
        return global.JetApi.demoExportTbFile({});
      }).then(function (tbFile) {
        return global.JetApi.importTbFromFile({
          filePath: tbFile.filePath,
          fileName: tbFile.fileName
        });
      }).then(function (tbImport) {
        applyResponse('tb', tbImport);
        return global.JetApi.demoExportAccountMappingFile({});
      }).then(function (amFile) {
        return global.JetApi.importAccountMappingFromFile({
          filePath: amFile.filePath,
          fileName: amFile.fileName
        });
      }).then(function (amImport) {
        Store.setAccountMappingState({
          batchId: amImport.batchId,
          rowCount: amImport.rowCount,
          fileName: amImport.fileName,
          importedUtc: amImport.importedUtc
        });
        return global.JetApi.demoExportAuthorizedPreparerFile({});
      }).then(function (apFile) {
        return global.JetApi.importAuthorizedPreparerFromFile({
          filePath: apFile.filePath,
          fileName: apFile.fileName
        });
      }).then(function (apImport) {
        Store.setAuthorizedPreparerState({
          batchId: apImport.batchId,
          rowCount: apImport.rowCount,
          fileName: apImport.fileName,
          importedUtc: apImport.importedUtc
        });
        return global.JetApi.importHoliday({ dates: demo.holidays });
      }).then(function (holidayResult) {
        return global.JetApi.importMakeupDay({ dates: demo.makeupDays }).then(function (makeupResult) {
          Store.setCalendarState({
            holidayCount: holidayResult.count,
            makeupDayCount: makeupResult.count
          });
        });
      }).then(function () {
        var state = Store.getState();
        Store.addMessage(
          '測試匯入完成：GL ' + state.importState.gl.rowCount + ' 列、TB ' +
          state.importState.tb.rowCount + ' 列、科目配對 ' +
          state.importState.accountMapping.rowCount + ' 個科目、授權編製人員 ' +
          (state.importState.authorizedPreparer ? state.importState.authorizedPreparer.rowCount : 0) + ' 位、假日 ' +
          state.importState.calendar.holidayCount + ' 天、補班 ' +
          state.importState.calendar.makeupDayCount + ' 天。', 'info');
      });
    });
  }

  Ui.registerStep('import', render);
  Ui.registerMockLoader('import', mockImportData);
})(window);
