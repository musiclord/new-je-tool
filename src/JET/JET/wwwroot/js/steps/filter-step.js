/*
  Step 4：進階條件篩選（條件 AST + Query Builder）。
  前端只組裝 AST 與渲染；條件由後端轉參數化 SQL 評估，前端不計算規則。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  // 鏡像閘門：科目配對／授權清單未匯入時不提供需要它的型別/預篩選鍵（權威驗證在後端）。
  function availableRuleTypes() {
    var imp = Store.getState().importState;
    return Ui.FILTER_RULE_TYPES.filter(function (t) {
      return (!t.requiresAccountMapping || !!imp.accountMapping) &&
        (!t.requiresAuthorizedPreparers || !!imp.authorizedPreparer);
    });
  }

  function availablePrescreenKeys() {
    var imp = Store.getState().importState;
    return Ui.PRESCREEN_KEY_OPTIONS.filter(function (o) {
      return (!o.requiresAccountMapping || !!imp.accountMapping) &&
        (!o.requiresAuthorizedPreparers || !!imp.authorizedPreparer);
    });
  }

  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('進階條件篩選');
      Ui.bindNoProjectPanel(container);
      return;
    }

    var draft = state.filter.draft;
    var saved = state.filter.savedScenarios;
    var prescreenRun = state.lastRuns.prescreen;
    var draftRuleCount = draft.groups.reduce(function (sum, g) { return sum + g.rules.length; }, 0);

    container.innerHTML =
      '<div class="panel panel--wide">' +
        '<h2 class="panel__title">進階條件篩選</h2>' +
        '<p class="panel__hint">以條件群組（AND / OR 由左至右結合）組合篩選情境，最多保存 10 個；' +
          '所有條件一律由後端在資料庫中計算，前端只負責組裝與顯示。</p>' +
        '<div class="stats-bar">' +
          '<div class="stat-card">' +
            '<span class="stat-card__value">' + (prescreenRun ? '已執行' : '未執行') + '</span>' +
            '<span class="stat-card__label">風險預篩選</span>' +
          '</div>' +
          '<div class="stat-card">' +
            '<span class="stat-card__value">' + draftRuleCount + '</span>' +
            '<span class="stat-card__label">草稿規則數</span>' +
          '</div>' +
          '<div class="stat-card">' +
            '<span class="stat-card__value">' + saved.length + ' / 10</span>' +
            '<span class="stat-card__label">已儲存情境</span>' +
          '</div>' +
        '</div>' +
        scenarioBuilderHtml(draft) +
        previewPaneHtml(state.filter.preview) +
        savedScenariosHtml(saved) +
        tagMatrixHtml(saved) +
        '<div class="panel__actions">' + Ui.MOCK_BUTTON_HTML + '</div>' +
        Ui.stepFooterHtml(state) +
      '</div>';

    bind(container);
  }

  // 快速加入：依審計意圖分組呈現（FILTER_RULE_GROUPS 順序），每組一個小標題＋該組可用型別鈕。
  // 只渲染目前可用（通過鏡像閘門）的型別；某組全數不可用時整組省略。
  function quickAddGroupsHtml() {
    var available = availableRuleTypes();
    return Ui.FILTER_RULE_GROUPS.map(function (grp) {
      var btns = available.filter(function (t) { return t.group === grp.key; }).map(function (t) {
        return '<button type="button" class="btn btn--ghost" data-action="add-rule" data-rule-type="' +
          t.value + '">+ ' + Ui.esc(t.quickLabel || t.label) + '</button>';
      }).join('');
      if (!btns) { return ''; }
      return '<div class="quick-add__group">' +
        '<span class="quick-add__group-label">' + Ui.esc(grp.label) + '</span>' +
        '<div class="quick-add__buttons">' + btns + '</div>' +
      '</div>';
    }).join('');
  }

  function scenarioBuilderHtml(draft) {
    var groupsHtml = draft.groups.length === 0
      ? '<p class="empty-state">尚未建立規則；用「快速加入」加入第一條條件，或套用測試案件。</p>'
      : draft.groups.map(function (group, gi) {
          var joinControl = gi === 0
            ? '<span class="scenario-group__order">第一組</span>'
            : '<select class="scenario-group__join" data-group-join="' + gi + '">' +
                '<option value="AND"' + (group.join !== 'OR' ? ' selected' : '') + '>AND</option>' +
                '<option value="OR"' + (group.join === 'OR' ? ' selected' : '') + '>OR</option>' +
              '</select>';

          var rules = group.rules.map(function (rule, ri) {
            return ruleRowHtml(rule, gi, ri);
          }).join('');

          return (
            '<section class="scenario-group">' +
              '<div class="scenario-group__head">' +
                '<span class="scenario-group__title">條件群組 ' + (gi + 1) + '</span>' +
                joinControl +
                '<button type="button" class="btn btn--ghost" data-action="remove-group" data-gi="' + gi +
                  '">移除群組</button>' +
              '</div>' +
              (rules || '<p class="empty-state">此群組沒有規則。</p>') +
            '</section>'
          );
        }).join('');

    return (
      '<section class="rule-card">' +
        '<h3 class="rule-card__title">建立篩選情境</h3>' +
        '<label class="form__row">' +
          '<span class="form__label">情境名稱 <em class="form__req">*</em></span>' +
          '<input class="form__input" type="text" data-bind="scenario-name" placeholder="例：摘要異常且金額偏高" value="' +
            Ui.esc(draft.name) + '">' +
        '</label>' +
        '<label class="form__row">' +
          '<span class="form__label">篩選動機說明 <em class="form__req">*</em></span>' +
          '<textarea class="form__input" rows="2" data-bind="scenario-rationale" placeholder="說明這個情境為何值得保留到工作底稿">' +
            Ui.esc(draft.rationale) + '</textarea>' +
        '</label>' +
        '<div class="quick-add">' +
          '<span class="quick-add__intro">快速加入條件 — 依用途分組，點一下即加入一條：</span>' +
          quickAddGroupsHtml() +
        '</div>' +
        groupsHtml +
        '<p class="form-notice" data-bind="scenario-notice" role="alert" hidden></p>' +
        '<div class="panel__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="add-group">新增條件群組</button>' +
          '<button type="button" class="btn btn--ghost" data-action="preview-population"' +
            ' title="開啟資料預覽：金額/日期範圍與資料樣貌，作為設定條件的參考">預覽測試母體</button>' +
          '<button type="button" class="btn" data-action="preview-scenario">預覽這個情境</button>' +
          '<button type="button" class="btn" data-action="save-scenario">保存為篩選情境</button>' +
        '</div>' +
      '</section>'
    );
  }

  function ruleRowHtml(rule, gi, ri) {
    var joinCell = ri === 0
      ? '<span class="rule-row__join rule-row__join--first">第 1 條</span>'
      : '<select class="rule-row__join" data-rule-bind="join">' +
          '<option value="AND"' + (rule.join !== 'OR' ? ' selected' : '') + '>AND</option>' +
          '<option value="OR"' + (rule.join === 'OR' ? ' selected' : '') + '>OR</option>' +
        '</select>';

    // 型別下拉以同一套四組（optgroup）呈現，與快速加入分組一致，強化分類語彙。
    var available = availableRuleTypes();
    var typeOptions = Ui.FILTER_RULE_GROUPS.map(function (grp) {
      var opts = available.filter(function (t) { return t.group === grp.key; }).map(function (t) {
        return '<option value="' + t.value + '"' + (rule.type === t.value ? ' selected' : '') + '>' +
          Ui.esc(t.label) + '</option>';
      }).join('');
      return opts ? '<optgroup label="' + Ui.esc(grp.label) + '">' + opts + '</optgroup>' : '';
    }).join('');

    return (
      '<div class="rule-row" data-gi="' + gi + '" data-ri="' + ri + '">' +
        joinCell +
        '<select class="rule-row__type" data-rule-bind="type">' + typeOptions + '</select>' +
        '<div class="rule-row__controls">' + ruleControlsHtml(rule) + '</div>' +
        '<button type="button" class="btn btn--ghost" data-action="remove-rule">移除</button>' +
      '</div>'
    );
  }

  function ruleControlsHtml(rule) {
    function fieldSelect(keys) {
      return '<select data-rule-bind="field">' + keys.map(function (key) {
        return '<option value="' + key + '"' + (rule.field === key ? ' selected' : '') + '>' +
          Ui.esc(Ui.glFieldLabel(key)) + '</option>';
      }).join('') + '</select>';
    }

    function categorySelect(bind, current) {
      return '<select data-rule-bind="' + bind + '">' + Ui.ACCOUNT_CATEGORY_OPTIONS.map(function (o) {
        return '<option value="' + Ui.esc(o.value) + '"' + (current === o.value ? ' selected' : '') + '>' +
          o.label + '</option>';
      }).join('') + '</select>';
    }

    switch (rule.type) {
      case 'prescreen':
        return '<select data-rule-bind="prescreenKey">' + availablePrescreenKeys().map(function (o) {
          return '<option value="' + o.value + '"' + (rule.prescreenKey === o.value ? ' selected' : '') + '>' +
            o.label + '</option>';
        }).join('') + '</select>';

      case 'text':
        return fieldSelect(Ui.FILTER_TEXT_FIELDS) +
          '<input type="text" data-rule-bind="keywords" placeholder="以逗號分隔多個關鍵字" value="' +
            Ui.esc(rule.keywords) + '">' +
          '<select data-rule-bind="mode">' + Ui.TEXT_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.mode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>';

      case 'dateRange':
        return fieldSelect(Ui.FILTER_DATE_FIELDS) +
          '<input type="date" data-rule-bind="from" value="' + Ui.esc(rule.from) + '">' +
          '<span class="rule-row__sep">～</span>' +
          '<input type="date" data-rule-bind="to" value="' + Ui.esc(rule.to) + '">';

      case 'numRange':
        return '<span class="rule-row__field-label" title="不分借貸，以金額大小比較（取絕對值）">金額（絕對值）</span>' +
          '<input type="number" data-rule-bind="from" placeholder="最小金額" value="' + Ui.esc(rule.from) + '">' +
          '<span class="rule-row__sep">～</span>' +
          '<input type="number" data-rule-bind="to" placeholder="最大金額" value="' + Ui.esc(rule.to) + '">';

      case 'drCrOnly':
        return '<select data-rule-bind="drCr">' +
          '<option value="debit"' + (rule.drCr !== 'credit' ? ' selected' : '') + '>僅借方</option>' +
          '<option value="credit"' + (rule.drCr === 'credit' ? ' selected' : '') + '>僅貸方</option>' +
        '</select>';

      case 'manualAuto':
        return '<select data-rule-bind="isManual">' +
          '<option value="true"' + (rule.isManual !== 'false' ? ' selected' : '') + '>人工分錄</option>' +
          '<option value="false"' + (rule.isManual === 'false' ? ' selected' : '') + '>自動分錄</option>' +
        '</select>';

      case 'accountPair':
        return '<select data-rule-bind="pairMode">' + Ui.ACCOUNT_PAIR_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.pairMode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>' +
          '<span class="rule-row__sep">借方</span>' + categorySelect('debitCategory', rule.debitCategory) +
          '<span class="rule-row__sep">貸方</span>' + categorySelect('creditCategory', rule.creditCategory);

      case 'periodInOut':
        return '<select data-rule-bind="inPeriod">' +
          '<option value="true"' + (rule.inPeriod !== 'false' ? ' selected' : '') + '>總帳日期在查核期間內</option>' +
          '<option value="false"' + (rule.inPeriod === 'false' ? ' selected' : '') + '>總帳日期在查核期間外</option>' +
        '</select>';

      case 'customKeywords':
        return '<span class="rule-row__field-label">摘要含</span>' +
          '<input type="text" data-rule-bind="keywords" placeholder="以逗號分隔多個關鍵字" value="' +
            Ui.esc(rule.keywords) + '">';

      case 'customTrailingZeros':
        return '<span class="rule-row__field-label" title="金額末幾位連續為 0；位數越多越接近整數">尾數連續 0 位數</span>' +
          '<input type="number" data-rule-bind="digits" min="1" max="12" step="1" value="' +
            Ui.esc(rule.digits) + '">';

      case 'customPreparerEntryCount':
        return '<span class="rule-row__field-label">編製人員全期張數 ≤</span>' +
          '<input type="number" data-rule-bind="maxEntries" min="1" step="1" value="' +
            Ui.esc(rule.maxEntries) + '">';

      case 'customAccountEntryCount':
        return '<span class="rule-row__field-label">科目全期張數 ≤</span>' +
          '<input type="number" data-rule-bind="maxEntries" min="1" step="1" value="' +
            Ui.esc(rule.maxEntries) + '">';

      default:
        return '';
    }
  }

  function ruleSummaryLabel(rule, index) {
    var prefix = index === 0 ? '' : '(' + (rule.join === 'OR' ? 'OR' : 'AND') + ') ';
    switch (rule.type) {
      case 'prescreen': {
        var hit = Ui.PRESCREEN_KEY_OPTIONS.filter(function (o) { return o.value === rule.prescreenKey; })[0];
        return prefix + '預篩選：' + (hit ? hit.label : rule.prescreenKey);
      }
      case 'text': {
        var mode = Ui.TEXT_MODE_OPTIONS.filter(function (o) { return o.value === rule.mode; })[0];
        return prefix + Ui.glFieldLabel(rule.field) + ' ' + (mode ? mode.label : rule.mode) +
          '「' + rule.keywords + '」';
      }
      case 'dateRange':
        return prefix + Ui.glFieldLabel(rule.field) + ' ' + (rule.from || '…') + '～' + (rule.to || '…');
      case 'numRange':
        return prefix + '金額（絕對值） ' + (rule.from ? '≥ ' + rule.from : '') +
          (rule.from && rule.to ? '、' : '') + (rule.to ? '≤ ' + rule.to : '');
      case 'drCrOnly':
        return prefix + (rule.drCr === 'credit' ? '僅貸方' : '僅借方');
      case 'manualAuto':
        return prefix + (rule.isManual === 'false' ? '自動分錄' : '人工分錄');
      case 'accountPair': {
        var pairMode = Ui.ACCOUNT_PAIR_MODE_OPTIONS.filter(function (o) { return o.value === rule.pairMode; })[0];
        return prefix + '科目配對分析：' + (pairMode ? pairMode.label : rule.pairMode) +
          '（借方 ' + rule.debitCategory + '・貸方 ' + rule.creditCategory + '）';
      }
      case 'periodInOut':
        return prefix + (rule.inPeriod === 'false' ? '總帳日期在查核期間外' : '總帳日期在查核期間內');
      case 'customKeywords':
        return prefix + '自訂關鍵字「' + rule.keywords + '」';
      case 'customTrailingZeros':
        return prefix + '尾數連續 ' + rule.digits + ' 個 0';
      case 'customPreparerEntryCount':
        return prefix + '編製人員全期張數 ≤ ' + rule.maxEntries;
      case 'customAccountEntryCount':
        return prefix + '科目全期張數 ≤ ' + rule.maxEntries;
      default:
        return prefix + rule.type;
    }
  }

  function scenarioPillsHtml(groups) {
    return (groups || []).map(function (group, gi) {
      var pills = group.rules.map(function (rule, ri) {
        return '<span class="scenario-pill">' + Ui.esc(ruleSummaryLabel(rule, ri)) + '</span>';
      }).join('');
      var head = gi === 0 ? '' :
        '<span class="scenario-pill scenario-pill--join">' + (group.join === 'OR' ? 'OR' : 'AND') + '</span>';
      return head + '<span class="scenario-pill scenario-pill--group">群組 ' + (gi + 1) + '</span>' + pills;
    }).join('');
  }

  function previewPaneHtml(preview) {
    var body;
    if (!preview) {
      body = '<p class="empty-state">先建立規則，再按「預覽這個情境」。</p>';
    } else {
      body =
        '<p class="rule-card__sub">命中 ' + Number(preview.count).toLocaleString() + ' 筆／' +
          Number(preview.voucherCount).toLocaleString() + ' 張傳票；以下為前 ' +
          preview.previewRows.length + ' 筆預覽。</p>' +
        '<div class="preview-table__wrap">' + Ui.previewTableHtml(preview.previewRows) + '</div>';
    }

    return (
      '<section class="rule-card">' +
        '<h3 class="rule-card__title">預覽結果</h3>' +
        body +
      '</section>'
    );
  }

  function savedScenariosHtml(saved) {
    var items = saved.length === 0
      ? '<p class="empty-state">尚未保存任何篩選情境。先預覽，再決定是否保留到工作底稿。</p>'
      : saved.map(function (s, i) {
          return (
            '<article class="saved-scenario">' +
              '<div class="saved-scenario__head">' +
                '<span class="saved-scenario__name">' + (i + 1) + '. ' + Ui.esc(s.name) + '</span>' +
                '<button type="button" class="btn btn--ghost" data-action="toggle-scenario" data-index="' + i +
                  '">詳情</button>' +
                '<button type="button" class="btn btn--ghost" data-action="remove-scenario" data-index="' + i +
                  '">移除</button>' +
              '</div>' +
              '<div class="saved-scenario__body" data-bind="scenario-body-' + i + '" hidden>' +
                '<p class="rule-card__sub">' + Ui.esc(s.rationale) + '</p>' +
                '<div class="scenario-pills">' + scenarioPillsHtml(s.groups) + '</div>' +
                '<div class="saved-scenario__preview" data-bind="scenario-preview-' + i + '"></div>' +
              '</div>' +
            '</article>'
          );
        }).join('');

    return (
      '<section class="rule-card">' +
        '<h3 class="rule-card__title">已儲存篩選情境' +
          '<span class="check-item__count">' + saved.length + ' / 10</span></h3>' +
        items +
      '</section>'
    );
  }

  /* ---- 高風險條件矩陣（D2 預覽：step3 摘要 + step4 傳票矩陣 + step4-1 行層） ----
     全程零商業邏輯：只把 wire 回來的 matchedPositions 對映成 C 欄 ✓／空白、組表；
     查詢/pivot/計算/SQL 全在後端三 action（tagMatrixScenarios/VoucherPage/RowPage）。 */

  // 摺疊外殼；展開時惰性載入(bind() 的 toggle 處理)。無已存情境時不顯示展開鈕,直接友善空狀態。
  function tagMatrixHtml(saved) {
    if (saved.length === 0) {
      return (
        '<section class="rule-card">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<p class="empty-state">尚未保存任何篩選情境；先在上方保存情境，矩陣會把每個情境當成一個高風險條件欄（C1..CN）。</p>' +
        '</section>'
      );
    }

    return (
      '<section class="rule-card">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<button type="button" class="btn btn--ghost" data-action="toggle-matrix">展開矩陣</button>' +
        '</div>' +
        '<p class="rule-card__sub">把全部已存情境當成高風險條件 C1..CN：情境摘要（各條件命中數）、' +
          '傳票矩陣（每張命中傳票打 ✓ 哪些條件）、行層明細（逐行 tag）。皆由後端即時算出，前端只顯示。</p>' +
        '<div class="saved-scenario__body" data-bind="matrix-body" hidden>' +
          '<div data-bind="matrix-scenarios"></div>' +
          '<div data-bind="matrix-vouchers"></div>' +
          '<div data-bind="matrix-rows"></div>' +
        '</div>' +
      '</section>'
    );
  }

  // 由情境摘要組「C 欄定義」：依 position 升冪,每欄 { position, label:'C{p}', name }。
  // C 欄欄序與標頭一律以此為準(brief：依 tagMatrixScenarios 回的 position 升冪)。
  function buildTagColumns(scenarios) {
    return (scenarios || [])
      .slice()
      .sort(function (a, b) { return a.position - b.position; })
      .map(function (s) {
        return { position: s.position, label: 'C' + s.position, name: s.name };
      });
  }

  // 情境摘要表(step3 交叉參考)：位置/條件名稱(=C 欄)/傳票命中數/行命中數。
  function scenarioSummaryTableHtml(columns) {
    var rows = columns.map(function (c) {
      var s = c.summary;
      return (
        '<tr>' +
          '<td>' + Ui.esc(c.label) + '</td>' +
          '<td>' + Ui.esc(s.name) + '</td>' +
          '<td class="preview-table__amount">' + Number(s.voucherHitCount).toLocaleString() + '</td>' +
          '<td class="preview-table__amount">' + Number(s.rowHitCount).toLocaleString() + '</td>' +
        '</tr>'
      );
    }).join('');

    return (
      '<h4 class="rule-card__title">情境摘要</h4>' +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>欄</th><th>高風險條件（情境名稱）</th><th>命中傳票數</th><th>命中行數</th></tr></thead>' +
          '<tbody>' + rows + '</tbody>' +
        '</table>' +
      '</div>'
    );
  }

  // 動態 C 欄的標頭片段:C{p},以情境名稱當 tooltip(title)。
  function tagColumnHeadHtml(columns) {
    return columns.map(function (c) {
      return '<th title="' + Ui.esc(c.name) + '">' + Ui.esc(c.label) + '</th>';
    }).join('');
  }

  // 把 matchedPositions(陣列)對映成每個 C 欄的 ✓／空白 column 取值函式(供 appendRowsToTbody)。
  // 純對映:matchedPositions 含該欄 position → '✓',否則空白。不做任何查詢/計算。
  function tagCellColumns(columns) {
    return columns.map(function (c) {
      return { cell: function (row) {
        var hit = (row.matchedPositions || []).indexOf(c.position) >= 0;
        return hit ? '✓' : '';
      }, className: 'preview-table__tag' };
    });
  }

  // 預覽／保存前的前端引導：名稱、動機、至少一條件未補齊時，於欄位旁（紅框＋紅字）
  // 與按鈕上方（form-notice）就近提示並不送出。純呈現——後端 invalid_scenario 仍是權威。
  function scenarioGate(container) {
    var draft = Store.getState().filter.draft;
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    var notice = container.querySelector('[data-bind="scenario-notice"]');

    Ui.clearFieldError(nameInput);
    Ui.clearFieldError(rationaleInput);

    var nameEmpty = !draft.name || !draft.name.trim();
    var rationaleEmpty = !draft.rationale || !draft.rationale.trim();
    var noCondition = draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; });

    var problems = [];
    if (nameEmpty) { Ui.setFieldError(nameInput, '請先填寫情境名稱'); problems.push('情境名稱'); }
    if (rationaleEmpty) { Ui.setFieldError(rationaleInput, '請先填寫篩選動機說明'); problems.push('篩選動機'); }
    if (noCondition) { problems.push('至少一條篩選條件'); }

    if (problems.length === 0) {
      if (notice) { notice.hidden = true; notice.textContent = ''; }
      return true;
    }
    if (notice) {
      notice.textContent = '尚需補齊：' + problems.join('、');
      notice.hidden = false;
    }
    // 聚焦第一個有問題的欄位（名稱 → 動機）；只缺條件時不搶焦點。
    if (nameEmpty) { nameInput.focus(); }
    else if (rationaleEmpty) { rationaleInput.focus(); }
    return false;
  }

  // 使用者輸入時放寬（NN/g：別在打字途中責備）——只清除已補齊欄位的錯誤、
  // 全部補齊時收起按鈕旁提示；不在此新增任何錯誤。
  function softRefreshGate(container) {
    var draft = Store.getState().filter.draft;
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    var notice = container.querySelector('[data-bind="scenario-notice"]');

    if (draft.name && draft.name.trim()) { Ui.clearFieldError(nameInput); }
    if (draft.rationale && draft.rationale.trim()) { Ui.clearFieldError(rationaleInput); }

    var noCondition = draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; });
    var stillMissing = (!draft.name || !draft.name.trim()) ||
      (!draft.rationale || !draft.rationale.trim()) || noCondition;
    if (!stillMissing && notice) { notice.hidden = true; notice.textContent = ''; }
  }

  function bind(container) {
    Ui.bindMockButton(container, 'filter');
    Ui.bindStepFooter(container);

    // 名稱／動機：值編輯只 patch（notify），不重建面板（保住輸入焦點）。
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    nameInput.addEventListener('input', function () {
      Store.patchFilterDraftMeta({ name: nameInput.value });
      softRefreshGate(container);
    });
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    rationaleInput.addEventListener('input', function () {
      Store.patchFilterDraftMeta({ rationale: rationaleInput.value });
      softRefreshGate(container);
    });

    container.querySelectorAll('[data-action="add-rule"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        if (draft.groups.length === 0) {
          draft.groups.push({ join: 'AND', rules: [] });
        }
        draft.groups[draft.groups.length - 1].rules.push(
          Ui.newFilterRule(btn.getAttribute('data-rule-type')));
        Store.setFilterDraft(draft);
      });
    });

    container.querySelector('[data-action="add-group"]').addEventListener('click', function () {
      var draft = Store.getState().filter.draft;
      draft.groups.push({ join: 'AND', rules: [] });
      Store.setFilterDraft(draft);
    });

    // 換位到資料預覽：設定條件前掌握測試母體的數值/日期/摘要樣貌
    var populationBtn = container.querySelector('[data-action="preview-population"]');
    if (populationBtn && Ui.openDataPreview) {
      populationBtn.addEventListener('click', function () {
        Ui.openDataPreview('glEntries');
      });
    }

    container.querySelectorAll('[data-action="remove-group"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        draft.groups.splice(Number(btn.getAttribute('data-gi')), 1);
        Store.setFilterDraft(draft);
      });
    });

    container.querySelectorAll('[data-group-join]').forEach(function (select) {
      select.addEventListener('change', function () {
        var draft = Store.getState().filter.draft;
        draft.groups[Number(select.getAttribute('data-group-join'))].join = select.value;
      });
    });

    container.querySelectorAll('.rule-row').forEach(function (row) {
      var gi = Number(row.getAttribute('data-gi'));
      var ri = Number(row.getAttribute('data-ri'));

      row.querySelector('[data-action="remove-rule"]').addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        draft.groups[gi].rules.splice(ri, 1);
        Store.setFilterDraft(draft);
      });

      row.querySelectorAll('[data-rule-bind]').forEach(function (control) {
        control.addEventListener('change', function () {
          var key = control.getAttribute('data-rule-bind');
          if (key === 'type') {
            // 型別切換是結構變動：重設該型別的預設欄位並重建列。
            var draft = Store.getState().filter.draft;
            var fresh = Ui.newFilterRule(control.value);
            fresh.join = draft.groups[gi].rules[ri].join;
            draft.groups[gi].rules[ri] = fresh;
            Store.setFilterDraft(draft);
            return;
          }
          var patch = {};
          patch[key] = control.value;
          Store.patchFilterRule(gi, ri, patch);
        });
      });
    });

    container.querySelector('[data-action="preview-scenario"]').addEventListener('click', function () {
      if (!scenarioGate(container)) { return; }
      var draft = Store.getState().filter.draft;

      Ui.run('預覽篩選情境', function () {
        return global.JetApi.filterPreview({ scenario: draft }).then(function (data) {
          Store.setFilterPreview(data.scenario);
          Store.addMessage('情境預覽：命中 ' + data.scenario.count + ' 筆／' +
            data.scenario.voucherCount + ' 張傳票。', 'info');
        });
      });
    });

    container.querySelector('[data-action="save-scenario"]').addEventListener('click', function () {
      if (!scenarioGate(container)) { return; }
      var current = Store.getState().filter;

      var scenarios = current.savedScenarios.map(function (s) {
        return { name: s.name, rationale: s.rationale, groups: s.groups };
      }).concat([current.draft]);

      if (scenarios.length > 10) {
        Store.addMessage('最多保存 10 個篩選情境；請先移除既有情境。', 'warn');
        return;
      }

      Ui.run('保存篩選情境', function () {
        return global.JetApi.filterCommit({ scenarios: scenarios }).then(function (data) {
          Store.setSavedScenarios(scenarios);
          Store.addMessage('已保存篩選情境（' + data.savedCount + ' / 10）。', 'info');
        });
      });
    });

    container.querySelectorAll('[data-action="toggle-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = btn.getAttribute('data-index');
        var body = container.querySelector('[data-bind="scenario-body-' + index + '"]');
        if (!body) { return; }

        body.hidden = !body.hidden;
        btn.textContent = body.hidden ? '詳情' : '收合';
        if (body.hidden) { return; }

        // 首次展開時惰性載入該情境的命中預覽（重用既有 filter.preview，顯示前 10 筆）；
        // 以預覽容器的 data-loaded 旗標當快取，重複展開不重抓。
        var preview = body.querySelector('[data-bind="scenario-preview-' + index + '"]');
        if (!preview || preview.getAttribute('data-loaded') === '1') { return; }

        var scenario = Store.getState().filter.savedScenarios[Number(index)];
        if (!scenario) { return; }

        // scenarioPosition 對齊後端 1-based position(保存順序);用於 query.filterHitsPage 接續行層明細。
        var scenarioPosition = Number(index) + 1;

        preview.textContent = '載入中…';
        Ui.run('預覽已儲存情境', function () {
          return global.JetApi.filterPreview({
            scenario: { name: scenario.name, rationale: scenario.rationale, groups: scenario.groups }
          }).then(function (data) {
            var s = data.scenario;
            var rows = s.previewRows.slice(0, 10);
            preview.innerHTML =
              '<p class="rule-card__sub">命中 ' + Number(s.count).toLocaleString() + ' 筆／' +
                Number(s.voucherCount).toLocaleString() + ' 張傳票，以下為前 ' + rows.length + ' 筆。</p>' +
              '<div class="preview-table__wrap">' + Ui.previewTableHtml(rows) + '</div>' +
              '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more" data-action="hits-load-more">載入更多</button>';
            preview.setAttribute('data-loaded', '1');

            // 「載入更多」逐頁接續命中行層明細。tbody = 預覽表 tbody;欄序與 previewTableHtml 一致。
            // 純呼叫膠水:bindLoadMore 帶 cursor 發 query.filterHitsPage、append 列、到底移除鈕;前端不判命中。
            var tbody = preview.querySelector('.preview-table tbody');
            var moreBtn = preview.querySelector('[data-action="hits-load-more"]');
            Ui.bindLoadMore(moreBtn, function (cursor) {
              return global.JetApi.queryFilterHitsPage({
                scenarioPosition: scenarioPosition, cursor: cursor, pageSize: 200
              });
            }, function (hitRows) {
              Ui.appendRowsToTbody(tbody, hitRows, [
                function (r) { return r.documentNumber; },
                function (r) { return r.lineItem || '—'; },
                function (r) { return r.postDate; },
                function (r) { return (r.accountCode || '') + ' ' + (r.accountName || ''); },
                function (r) { return r.description || ''; },
                { cell: function (r) { return Number(r.amount).toLocaleString(); },
                  className: 'preview-table__amount' },
                function (r) { return r.drCr === 'DEBIT' ? '借' : '貸'; }
              ]);
            }, function () {
              // 首擊清掉預覽前 10 列(filter.preview 另一套排序),改接 keyset ASC 全量,避免重複與排序不一致。
              // 清列後全列皆來自 filterHits(欄位讀 r.description,與接列一致),預覽的 documentDescription 耦合自然消失。
              tbody.innerHTML = '';
            });
          });
        });
      });
    });

    container.querySelectorAll('[data-action="remove-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        var remaining = Store.getState().filter.savedScenarios
          .filter(function (_, i) { return i !== index; })
          .map(function (s) { return { name: s.name, rationale: s.rationale, groups: s.groups }; });

        Ui.run('移除篩選情境', function () {
          return global.JetApi.filterCommit({ scenarios: remaining }).then(function () {
            Store.setSavedScenarios(remaining);
            Store.addMessage('已移除情境（剩餘 ' + remaining.length + ' / 10）。', 'info');
          });
        });
      });
    });

    bindTagMatrix(container);
  }

  // 高風險條件矩陣:展開時惰性載入(摘要 → 組 C 欄定義 → 傳票矩陣 + 行層,各自載入更多)。
  // 以 matrix-body 的 data-loaded 旗標當快取,重複展開不重抓。零商業邏輯:只發 action、組表、對映 ✓。
  function bindTagMatrix(container) {
    var toggle = container.querySelector('[data-action="toggle-matrix"]');
    if (!toggle) { return; }

    toggle.addEventListener('click', function () {
      var body = container.querySelector('[data-bind="matrix-body"]');
      if (!body) { return; }

      body.hidden = !body.hidden;
      toggle.textContent = body.hidden ? '展開矩陣' : '收合矩陣';
      if (body.hidden || body.getAttribute('data-loaded') === '1') { return; }

      var scenariosBox = body.querySelector('[data-bind="matrix-scenarios"]');
      var vouchersBox = body.querySelector('[data-bind="matrix-vouchers"]');
      var rowsBox = body.querySelector('[data-bind="matrix-rows"]');
      scenariosBox.textContent = '載入中…';
      vouchersBox.textContent = '';
      rowsBox.textContent = '';

      Ui.run('載入高風險條件矩陣', function () {
        return global.JetApi.queryTagMatrixScenarios({}).then(function (data) {
          var columns = buildTagColumns(data.scenarios || []);
          // 把摘要掛回欄定義供摘要表顯示名稱與命中數。
          columns.forEach(function (c) {
            c.summary = (data.scenarios || []).filter(function (s) { return s.position === c.position; })[0];
          });

          if (columns.length === 0) {
            scenariosBox.innerHTML =
              '<p class="empty-state">尚無可用情境;請先在上方保存篩選情境。</p>';
            return;
          }

          scenariosBox.innerHTML = scenarioSummaryTableHtml(columns);
          renderVoucherMatrix(vouchersBox, columns);
          renderRowMatrix(rowsBox, columns);
          body.setAttribute('data-loaded', '1');
        });
      });
    });
  }

  // 傳票矩陣(step4):固定欄(傳票號/總帳日/編製者/傳票總額)+ 動態 C 欄;首屏即接第一頁,載入更多續接。
  // 矩陣為全新表(無預覽列混排),故首擊不需清預覽(bindLoadMore 的 clearTarget 省略)。
  function renderVoucherMatrix(box, columns) {
    box.innerHTML =
      '<h4 class="rule-card__title">傳票矩陣（step4：每張命中傳票打 ✓ 哪些條件）</h4>' +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>傳票號碼</th><th>總帳日期</th><th>編製人員</th>' +
            '<th>傳票總額</th>' + tagColumnHeadHtml(columns) + '</tr></thead>' +
          '<tbody></tbody>' +
        '</table>' +
      '</div>' +
      '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more"' +
        ' data-action="matrix-vouchers-more">載入更多</button>';

    var tbody = box.querySelector('.preview-table tbody');
    var fixedCols = [
      function (r) { return r.documentNumber; },
      function (r) { return r.postDate; },
      function (r) { return r.createdBy || ''; },
      { cell: function (r) { return Number(r.voucherTotal).toLocaleString(); },
        className: 'preview-table__amount' }
    ];
    var allCols = fixedCols.concat(tagCellColumns(columns));

    Ui.bindLoadMore(box.querySelector('[data-action="matrix-vouchers-more"]'), function (cursor) {
      return global.JetApi.queryTagMatrixVoucherPage({ cursor: cursor, pageSize: 200 });
    }, function (rows) {
      Ui.appendRowsToTbody(tbody, rows, allCols);
    });
  }

  // 行層明細(step4-1):命中傳票之所有行(含未命中行);無 per-voucher 過濾(Task 4 設計)——
  // 整體命中傳票行明細表 + 傳票號欄供對照 + 載入更多。固定欄 + 動態逐行 C tag。
  function renderRowMatrix(box, columns) {
    box.innerHTML =
      '<h4 class="rule-card__title">行層明細（step4-1：命中傳票之所有行，逐行 tag）</h4>' +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>傳票號碼</th><th>項次</th><th>總帳日期</th><th>科目</th>' +
            '<th>金額</th><th>摘要</th>' + tagColumnHeadHtml(columns) + '</tr></thead>' +
          '<tbody></tbody>' +
        '</table>' +
      '</div>' +
      '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more"' +
        ' data-action="matrix-rows-more">載入更多</button>';

    var tbody = box.querySelector('.preview-table tbody');
    var fixedCols = [
      function (r) { return r.documentNumber; },
      function (r) { return r.lineItem || '—'; },
      function (r) { return r.postDate; },
      function (r) { return (r.accountCode || '') + ' ' + (r.accountName || ''); },
      { cell: function (r) { return Number(r.amount).toLocaleString(); },
        className: 'preview-table__amount' },
      function (r) { return r.description || ''; }
    ];
    var allCols = fixedCols.concat(tagCellColumns(columns));

    Ui.bindLoadMore(box.querySelector('[data-action="matrix-rows-more"]'), function (cursor) {
      return global.JetApi.queryTagMatrixRowPage({ cursor: cursor, pageSize: 200 });
    }, function (rows) {
      Ui.appendRowsToTbody(tbody, rows, allCols);
    });
  }

  function mockApplyFilterScenario() {
    var state = Store.getState();
    if (!state.mapping.gl.committed) {
      Store.addMessage('請先完成欄位配對（可用前面步驟的測試案件按鈕）。', 'warn');
      return;
    }

    Ui.run('套用測試案件（進階篩選）', function () {
      var demoScenario;
      return global.JetApi.projectLoadDemo({}).then(function (demo) {
        demoScenario = demo.demoScenario;
        Store.setFilterDraft(JSON.parse(JSON.stringify(demoScenario)));
        return global.JetApi.filterPreview({ scenario: demoScenario });
      }).then(function (data) {
        Store.setFilterPreview(data.scenario);
        return global.JetApi.filterCommit({ scenarios: [demoScenario] });
      }).then(function (commit) {
        Store.setSavedScenarios([demoScenario]);
        var preview = Store.getState().filter.preview;
        Store.addMessage(
          '示範情境已預覽並保存（取代既有清單）：命中 ' + preview.count + ' 筆／' +
          preview.voucherCount + ' 張傳票，已儲存 ' + commit.savedCount + ' / 10。', 'info');
      });
    });
  }

  Ui.registerStep('filter', render);
  Ui.registerMockLoader('filter', mockApplyFilterScenario);
})(window);
