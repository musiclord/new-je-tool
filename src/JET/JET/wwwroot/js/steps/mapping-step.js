/*
  Step 2：欄位配對。
  來源欄位 → JET 邏輯欄位的對應；自動建議與提交標準化都在後端執行。

  每個資料集（GL/TB）的狀態模型（消除「已提交綠字 + 仍可按確認」的語意衝突）：
    未匯入        → 警示（請先匯入資料）
    草稿          → 編輯表格 + 自動建議/預覽來源資料/確認配對
    已提交        → 收合摘要卡（模式、標準化列數、提交時間、key→欄名清單）；
                    只有「重新配對」「預覽標準化資料」兩個動作，編輯表格不渲染
    草稿偏離      → 編輯表格 + 「修改尚未生效」橫幅 + 重新確認配對/還原為已提交版本
    來源變更失效  → 草稿 + 「來源資料已變更，原配對已失效」橫幅
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  // 「重新配對」的本地旗標：已提交且草稿未偏離時仍顯示編輯畫面。
  var editing = { gl: false, tb: false };

  // 來源原貌預覽快取（每資料集一份，依 batchId 失效）。
  // 形狀：null | { batchId, loading:true } | { batchId, error:true } | { batchId, columns, rows, totalCount }
  var sourceCache = { gl: null, tb: null };

  Ui.registerWorkflowReset(function () {
    editing = { gl: false, tb: false };
    sourceCache = { gl: null, tb: null };
  });

  // 當前模式下可由「來源欄」指派的 JET 欄位（排除字面值欄）。
  function assignableFields(fields, mode) {
    return fields.filter(function (f) {
      if (f.literal) { return false; }
      if (f.req === 'always' || f.req === 'optional') { return true; }
      return Array.isArray(f.req) && f.req.indexOf(mode) >= 0;
    });
  }

  // 反推：某來源欄目前被指派給哪個 JET 欄位（排除字面值欄，避免字面值剛好等於欄名時誤判）。
  function fieldForColumn(fields, mappingState, column) {
    var literalKeys = {};
    fields.forEach(function (f) { if (f.literal) { literalKeys[f.key] = true; } });
    var draft = mappingState.draft;
    return Object.keys(draft).filter(function (k) {
      return !literalKeys[k] && draft[k] === column;
    })[0] || '';
  }

  // 當前模式適用的字面值欄（GL side/flag 的 dcDebitCode；TB 無，回 null）。
  function literalFieldFor(fields, mode) {
    return fields.filter(function (f) {
      return f.literal && Array.isArray(f.req) && f.req.indexOf(mode) >= 0;
    })[0] || null;
  }

  // 當前模式下尚未指派的必填欄位（含字面值必填欄）。
  function missingRequired(fields, mode, mappingState) {
    return fields.filter(function (f) {
      return Ui.isRequired(f, mode) && !mappingState.draft[f.key];
    });
  }

  // 切模式：清掉不適用新模式的金額類欄位指派（否則標頭下拉顯示不到的殘留會留在 draft），再設模式。
  function changeMode(kind, fields, mode) {
    var draft = Store.getState().mapping[kind].draft;
    var pruned = {};
    Object.keys(draft).forEach(function (k) {
      var f = fields.filter(function (x) { return x.key === k; })[0];
      if (!f) { return; }
      if (Array.isArray(f.req) && f.req.indexOf(mode) < 0) { return; }
      pruned[k] = draft[k];
    });
    Store.replaceMappingDraft(kind, pruned);
    Store.setMappingMode(kind, mode);
  }

  // 惰性載入來源原貌前 10 列；載入完成後 touch() 重建面板顯示資料。
  function ensureSourcePreview(kind, importInfo) {
    if (!importInfo) { return; }
    var c = sourceCache[kind];
    if (c && c.batchId === importInfo.batchId && (c.loading || c.rows || c.error)) { return; }
    sourceCache[kind] = { batchId: importInfo.batchId, loading: true };
    global.JetApi.queryDataPreview({ dataset: kind === 'gl' ? 'glStaging' : 'tbStaging', limit: 10 })
      .then(function (data) {
        sourceCache[kind] = {
          batchId: importInfo.batchId,
          columns: data.columns || [],
          rows: data.rows || [],
          totalCount: data.totalCount || 0
        };
        Store.touch();
      })
      .catch(function () {
        sourceCache[kind] = { batchId: importInfo.batchId, error: true };
        Store.touch();
      });
  }

  function uiModeToggleHtml() {
    var uiMode = Store.getState().mappingUiMode;
    function btn(value, label) {
      return '<button type="button" class="seg-toggle__btn' + (uiMode === value ? ' is-active' : '') +
        '" data-ui-mode="' + value + '">' + label + '</button>';
    }
    return '<div class="seg-toggle" role="group" aria-label="配對介面">' +
      '<span class="seg-toggle__label">配對介面</span>' +
      btn('classic', '簡易清單') + btn('grid', '對照表格') +
      '</div>';
  }

  function bindUiModeToggle(container) {
    container.querySelectorAll('[data-ui-mode]').forEach(function (b) {
      b.addEventListener('click', function () {
        Store.setMappingUiMode(b.getAttribute('data-ui-mode'));
      });
    });
  }

  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('欄位配對');
      Ui.bindNoProjectPanel(container);
      return;
    }

    container.innerHTML =
      '<div class="panel panel--wide panel--mapping">' +
        '<h2 class="panel__title">欄位配對</h2>' +
        '<p class="panel__hint">將來源欄位對應到 JET 邏輯欄位。可先自動建議再人工確認；標 * 者為目前模式的必填欄位。</p>' +
        uiModeToggleHtml() +
        '<div class="panel__actions">' + Ui.MOCK_BUTTON_HTML + '</div>' +
        mappingSection('gl', 'GL 欄位配對', Ui.GL_FIELDS, Ui.GL_MODES,
          state.importState.gl, state.mapping.gl, state.mapping.gl.amountMode) +
        mappingSection('tb', 'TB 欄位配對', Ui.TB_FIELDS, Ui.TB_MODES,
          state.importState.tb, state.mapping.tb, state.mapping.tb.changeMode) +
        Ui.stepFooterHtml(state) +
      '</div>';

    bindUiModeToggle(container);
    Ui.bindMockButton(container, 'mapping');
    bindMappingSection(container, 'gl', Ui.GL_FIELDS);
    bindMappingSection(container, 'tb', Ui.TB_FIELDS);
    Ui.bindStepFooter(container);
  }

  /* ---- 狀態判定 -------------------------------------------------------------- */

  // 草稿（非空值）與已提交快照逐鍵相等，且模式一致。
  function draftMatchesCommitted(mappingState, mode) {
    var committed = mappingState.committed;
    if (!committed || !committed.mapping) { return false; }
    if (committed.mode && committed.mode !== mode) { return false; }

    var draftKeys = Object.keys(mappingState.draft).filter(function (k) { return mappingState.draft[k]; });
    var committedKeys = Object.keys(committed.mapping).filter(function (k) { return committed.mapping[k]; });
    if (draftKeys.length !== committedKeys.length) { return false; }

    return draftKeys.every(function (k) { return mappingState.draft[k] === committed.mapping[k]; });
  }

  function modeLabel(modes, mode) {
    var hit = modes.filter(function (m) { return m.value === mode; })[0];
    return hit ? hit.label : mode;
  }

  /* ---- 渲染 ------------------------------------------------------------------ */

  function mappingSection(kind, title, fields, modes, importInfo, mappingState, mode) {
    if (!importInfo) {
      return (
        '<section class="mapping-section">' +
          '<h3 class="mapping-section__title">' + title + '</h3>' +
          '<p class="mapping-section__warn">尚未匯入資料，請先完成「匯入資料」。</p>' +
        '</section>'
      );
    }

    var committed = mappingState.committed;
    var matches = draftMatchesCommitted(mappingState, mode);

    // 已提交且草稿未偏離、未按「重新配對」→ 收合摘要卡。
    if (committed && matches && !editing[kind]) {
      return summarySection(kind, title, fields, modes, importInfo, mappingState, mode);
    }

    return Store.getState().mappingUiMode === 'grid'
      ? gridEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches)
      : classicEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches);
  }

  function summarySection(kind, title, fields, modes, importInfo, mappingState, mode) {
    var committed = mappingState.committed;

    var facts = [modeLabel(modes, mode)];
    if (committed.projectedRowCount != null) {
      facts.push('已標準化 ' + Number(committed.projectedRowCount).toLocaleString() + ' 列');
    }
    if (committed.committedUtc) {
      facts.push('提交於 ' + new Date(committed.committedUtc).toLocaleString('zh-Hant', { hour12: false }));
    }

    return (
      '<section class="mapping-section" data-bind="mapping-' + kind + '">' +
        '<h3 class="mapping-section__title">' + title + '</h3>' +
        '<div class="mapping-summary">' +
          '<p class="mapping-summary__status">已提交，依此配對執行後續測試' +
            '<span class="mapping-summary__facts">' + facts.map(Ui.esc).join('・') + '</span></p>' +
          committedTableHtml(kind, fields, importInfo, committed) +
          literalNoteHtml(fields, committed) +
          '<div class="panel__actions">' +
            '<button type="button" class="btn btn--ghost" data-action="remap-' + kind + '">重新配對</button>' +
            '<button type="button" class="btn btn--ghost" data-action="preview-target-' + kind +
              '" title="開啟資料預覽，檢視標準化後資料">預覽標準化資料</button>' +
          '</div>' +
        '</div>' +
      '</section>'
    );
  }

  // 唯讀二維對照表:來源欄當表頭、其下標示對應到的 JET 欄位,再附樣本資料列(沿用 sourceCache)。
  function committedTableHtml(kind, fields, importInfo, committed) {
    var columns = (importInfo && importInfo.columns) || [];
    var span = Math.max(columns.length, 1);

    var assignRow = columns.map(function (col) {
      var field = fieldForCommitted(fields, committed.mapping, col);
      return '<th class="map-grid__cell' + (field ? ' is-assigned' : '') + '">' +
        (field ? Ui.esc(field.label) : '<span class="map-grid__unassigned">(未對應)</span>') +
        '</th>';
    }).join('');

    var headRow = columns.map(function (col) {
      return '<th class="map-grid__colname">' + Ui.esc(col) + '</th>';
    }).join('');

    var cache = sourceCache[kind];
    var bodyHtml;
    if (!cache || (importInfo && cache.batchId !== importInfo.batchId) || cache.loading) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">載入預覽中…</td></tr>';
    } else if (cache.error) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">預覽載入失敗，可重新進入此步驟再試。</td></tr>';
    } else if (!cache.rows.length) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">此批次尚無資料列。</td></tr>';
    } else {
      bodyHtml = cache.rows.map(function (row) {
        return '<tr>' + columns.map(function (col, i) {
          var cell = row[i];
          return '<td class="map-grid__data' + (cell == null ? ' is-null' : '') + '">' +
            (cell == null ? '∅' : Ui.esc(cell)) + '</td>';
        }).join('') + '</tr>';
      }).join('');
    }

    var note = '';
    if (cache && cache.rows && !cache.loading && !cache.error) {
      note = '<p class="map-grid__note">顯示前 ' + cache.rows.length + ' 列／共 ' +
        Number(cache.totalCount).toLocaleString() + ' 列</p>';
    }

    return '<div class="map-grid-wrap"><table class="map-grid">' +
      '<thead><tr class="map-grid__assign">' + assignRow + '</tr>' +
      '<tr class="map-grid__head">' + headRow + '</tr></thead>' +
      '<tbody>' + bodyHtml + '</tbody></table></div>' + note;
  }

  // 反查:某來源欄被哪個(非字面值)JET 欄位對應到(比對 committed.mapping)。
  function fieldForCommitted(fields, mappingObj, column) {
    var literalKeys = {};
    fields.forEach(function (f) { if (f.literal) { literalKeys[f.key] = true; } });
    var hitKey = Object.keys(mappingObj || {}).filter(function (k) {
      return !literalKeys[k] && mappingObj[k] === column;
    })[0];
    return hitKey ? fields.filter(function (f) { return f.key === hitKey; })[0] : null;
  }

  // 字面值欄(GL flag 的 dcDebitCode)不是來源欄,於表下補述其值。
  function literalNoteHtml(fields, committed) {
    var notes = fields.filter(function (f) {
      return f.literal && committed.mapping && committed.mapping[f.key];
    }).map(function (f) {
      return Ui.esc(f.label) + ' = ' + Ui.esc(committed.mapping[f.key]);
    });
    return notes.length
      ? '<p class="mapping-summary__literal">' + notes.join('・') + '</p>'
      : '';
  }

  function twoDimMappingTable(kind, fields, importInfo, mappingState, mode) {
    var columns = importInfo.columns || [];
    var assignable = assignableFields(fields, mode);
    var span = Math.max(columns.length, 1);

    var assignRow = columns.map(function (col) {
      var assigned = fieldForColumn(fields, mappingState, col);
      var opts = '<option value="">（不對應）</option>' + assignable.map(function (f) {
        return '<option value="' + f.key + '"' + (f.key === assigned ? ' selected' : '') + '>' +
          Ui.esc(f.label) + '</option>';
      }).join('');
      return '<th class="map-grid__cell' + (assigned ? ' is-assigned' : '') + '">' +
        '<select class="map-grid__select" data-map-col="' + Ui.esc(col) + '">' + opts + '</select>' +
        '</th>';
    }).join('');

    var headRow = columns.map(function (col) {
      return '<th class="map-grid__colname">' + Ui.esc(col) + '</th>';
    }).join('');

    var cache = sourceCache[kind];
    var bodyHtml;
    if (!cache || cache.batchId !== importInfo.batchId || cache.loading) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">載入預覽中…</td></tr>';
    } else if (cache.error) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">預覽載入失敗，可重新進入此步驟再試。</td></tr>';
    } else if (!cache.rows.length) {
      bodyHtml = '<tr><td class="map-grid__loading" colspan="' + span + '">此批次尚無資料列。</td></tr>';
    } else {
      bodyHtml = cache.rows.map(function (row) {
        return '<tr>' + columns.map(function (col, i) {
          var cell = row[i];
          return '<td class="map-grid__data' + (cell == null ? ' is-null' : '') + '">' +
            (cell == null ? '∅' : Ui.esc(cell)) + '</td>';
        }).join('') + '</tr>';
      }).join('');
    }

    var note = '';
    if (cache && cache.rows && !cache.loading && !cache.error) {
      note = '<p class="map-grid__note">顯示前 ' + cache.rows.length + ' 列／共 ' +
        Number(cache.totalCount).toLocaleString() + ' 列</p>';
    }

    return '<div class="map-grid-wrap"><table class="map-grid">' +
      '<thead><tr class="map-grid__assign">' + assignRow + '</tr>' +
      '<tr class="map-grid__head">' + headRow + '</tr></thead>' +
      '<tbody>' + bodyHtml + '</tbody></table></div>' + note;
  }

  // 二維表右側必填鐵軌：每個當前模式必填欄位一列；未指派灰「待指派」，已指派綠「✓ + 來源欄名」。
  function requiredRailHtml(fields, mode, mappingState) {
    var required = fields.filter(function (f) { return Ui.isRequired(f, mode); });
    var items = required.map(function (f) {
      var val = mappingState.draft[f.key];
      var done = !!val;
      var tail = done
        ? '<span class="map-rail__col">' + Ui.esc(val) + '</span>'
        : '<span class="map-rail__pending">待指派</span>';
      return '<li class="map-rail__item' + (done ? ' is-done' : '') + '">' +
        '<span class="map-rail__mark" aria-hidden="true">' + (done ? '✓' : '') + '</span>' +
        '<span class="map-rail__label">' + Ui.esc(f.label) + '</span>' +
        tail + '</li>';
    }).join('');

    var allDone = required.every(function (f) { return !!mappingState.draft[f.key]; });
    var title = allDone ? '必填欄位已全部指派' : '必填欄位';
    return '<aside class="map-rail' + (allDone ? ' is-complete' : '') + '">' +
      '<h4 class="map-rail__title">' + title + '</h4>' +
      '<ul class="map-rail__list">' + items + '</ul></aside>';
  }

  function gridEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches) {
    var banner = '';
    if (mappingState.invalidatedByImport) {
      banner = '<p class="mapping-section__warn">來源資料已變更，原配對已失效；請確認下方對應後重新提交。</p>';
    } else if (committed && !matches) {
      banner = '<p class="mapping-section__warn">下方修改尚未生效，目前仍以已提交版本執行；' +
        '按「重新確認配對」套用，或「還原為已提交版本」放棄修改。</p>';
    }

    var modeRadios = modes.map(function (m) {
      return '<label class="mode-option">' +
        '<input type="radio" name="mode-' + kind + '" value="' + m.value + '"' +
          (mode === m.value ? ' checked' : '') + '>' +
        '<span>' + m.label + '</span></label>';
    }).join('');

    var litField = literalFieldFor(fields, mode);
    var literalHtml = '';
    if (litField) {
      var litVal = mappingState.draft[litField.key] || '';
      var litReq = Ui.isRequired(litField, mode);
      literalHtml = '<div class="map-literal">' +
        '<label class="map-literal__label">' + Ui.esc(litField.label) +
          (litReq ? ' <em class="form__req">*</em>' : '') + '</label>' +
        '<input class="form__input map-literal__input" type="text" data-mapping-key="' + litField.key +
          '" value="' + Ui.esc(litVal) + '" placeholder="如 D 或 1">' +
        '<span class="map-literal__hint">借方的代碼字面值（不是欄位名稱）</span></div>';
    }

    var missing = missingRequired(fields, mode, mappingState);

    var actions =
      '<button type="button" class="btn btn--ghost" data-action="suggest-' + kind + '">自動建議</button>' +
      '<button type="button" class="btn" data-action="commit-' + kind + '"' +
        (missing.length ? ' disabled' : '') + '>' +
        (committed ? '重新確認配對' : '確認配對') + '</button>' +
      (committed
        ? '<button type="button" class="btn btn--ghost" data-action="restore-' + kind + '">還原為已提交版本</button>'
        : '');

    return '<section class="mapping-section" data-bind="mapping-' + kind + '">' +
        '<h3 class="mapping-section__title">' + title + '</h3>' +
        banner +
        '<div class="mode-group">' + modeRadios + '</div>' +
        '<div class="map-layout">' +
          '<div class="map-layout__main">' +
            twoDimMappingTable(kind, fields, importInfo, mappingState, mode) +
            literalHtml +
          '</div>' +
          requiredRailHtml(fields, mode, mappingState) +
        '</div>' +
        '<div class="panel__actions">' + actions + '</div>' +
      '</section>';
  }

  function classicEditSection(kind, title, fields, modes, importInfo, mappingState, mode, committed, matches) {
    var banner = '';
    if (mappingState.invalidatedByImport) {
      banner = '<p class="mapping-section__warn">來源資料已變更，原配對已失效；請確認下方對應後重新提交。</p>';
    } else if (committed && !matches) {
      banner = '<p class="mapping-section__warn">下方修改尚未生效，目前仍以已提交版本執行；' +
        '按「重新確認配對」套用，或「還原為已提交版本」放棄修改。</p>';
    }

    var modeRadios = modes.map(function (m) {
      return '<label class="mode-option">' +
        '<input type="radio" name="mode-' + kind + '" value="' + m.value + '"' +
          (mode === m.value ? ' checked' : '') + '>' +
        '<span>' + m.label + '</span></label>';
    }).join('');

    var rows = fields.map(function (field) {
      var required = Ui.isRequired(field, mode);
      var current = mappingState.draft[field.key] || '';

      var control;
      if (field.literal) {
        control = '<input class="form__input mapping-table__input" type="text" data-mapping-key="' + field.key +
          '" value="' + Ui.esc(current) + '" placeholder="如 D 或 1">';
      } else {
        var options = '<option value="">—</option>' + importInfo.columns.map(function (col) {
          return '<option value="' + Ui.esc(col) + '"' + (col === current ? ' selected' : '') + '>' +
            Ui.esc(col) + '</option>';
        }).join('');
        control = '<select class="mapping-table__select" data-mapping-key="' + field.key + '">' +
          options + '</select>';
      }

      return '<tr class="mapping-table__row' + (required ? ' is-required' : '') + '">' +
          '<td class="mapping-table__label">' + field.label +
            (required ? ' <em class="form__req">*</em>' : '') + '</td>' +
          '<td class="mapping-table__key">' + field.key + '</td>' +
          '<td>' + control + '</td>' +
        '</tr>';
    }).join('');

    var actions =
      '<button type="button" class="btn btn--ghost" data-action="suggest-' + kind + '">自動建議</button>' +
      '<button type="button" class="btn btn--ghost" data-action="preview-source-' + kind +
        '" title="開啟資料預覽，對照欄位名稱與實際內容">預覽來源資料</button>' +
      '<button type="button" class="btn" data-action="commit-' + kind + '">' +
        (committed ? '重新確認配對' : '確認配對') + '</button>' +
      (committed
        ? '<button type="button" class="btn btn--ghost" data-action="restore-' + kind + '">還原為已提交版本</button>'
        : '');

    return '<section class="mapping-section" data-bind="mapping-' + kind + '">' +
        '<h3 class="mapping-section__title">' + title + '</h3>' +
        banner +
        '<div class="mode-group">' + modeRadios + '</div>' +
        '<table class="mapping-table">' +
          '<thead><tr><th>JET 邏輯欄位</th><th>key</th><th>來源欄位</th></tr></thead>' +
          '<tbody>' + rows + '</tbody>' +
        '</table>' +
        '<div class="panel__actions">' + actions + '</div>' +
      '</section>';
  }

  /* ---- 事件 ------------------------------------------------------------------ */

  function bindMappingSection(container, kind, fields) {
    var section = container.querySelector('[data-bind="mapping-' + kind + '"]');
    if (!section) { return; }

    // 摘要卡動作：重新配對（展開編輯）、預覽標準化資料。
    var remapBtn = section.querySelector('[data-action="remap-' + kind + '"]');
    if (remapBtn) {
      remapBtn.addEventListener('click', function () {
        editing[kind] = true;
        Store.touch();
      });
    }

    var previewTargetBtn = section.querySelector('[data-action="preview-target-' + kind + '"]');
    if (previewTargetBtn && Ui.openDataPreview) {
      previewTargetBtn.addEventListener('click', function () {
        Ui.openDataPreview(kind === 'gl' ? 'glEntries' : 'tbBalances');
      });
    }

    // 標頭下拉：指派來源欄 → JET 欄位（維持一對一；字面值欄不視為欄位指派）。
    var literalKeys = fields.filter(function (f) { return f.literal; }).map(function (f) { return f.key; });
    section.querySelectorAll('[data-map-col]').forEach(function (sel) {
      sel.addEventListener('change', function () {
        Store.assignColumnToField(kind, sel.getAttribute('data-map-col'), sel.value, literalKeys);
      });
    });

    // 模式切換：清不適用欄位指派後設模式。
    section.querySelectorAll('input[name="mode-' + kind + '"]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        changeMode(kind, fields, radio.value);
      });
    });

    // 字面值輸入（grid/classic 共用）與 classic 的欄位下拉：寫入 draft。
    // INPUT（字面值）或已提交時重建——讓 grid 必填鐵軌與偏離橫幅即時更新；classic 的 SELECT 不重建以保流暢。
    section.querySelectorAll('[data-mapping-key]').forEach(function (control) {
      control.addEventListener('change', function () {
        Store.setMappingDraft(kind, control.getAttribute('data-mapping-key'), control.value.trim());
        if (control.tagName === 'INPUT' || Store.getState().mapping[kind].committed) { Store.touch(); }
      });
    });

    // 還原為已提交版本：草稿與模式回到快照，收回摘要卡。
    var restoreBtn = section.querySelector('[data-action="restore-' + kind + '"]');
    if (restoreBtn) {
      restoreBtn.addEventListener('click', function () {
        var committed = Store.getState().mapping[kind].committed;
        if (!committed || !committed.mapping) { return; }
        editing[kind] = false;
        Store.replaceMappingDraft(kind, Object.assign({}, committed.mapping));
        Store.setMappingMode(kind, committed.mode);
      });
    }

    // 草稿態才有二維表 → 惰性載入來源原貌。
    if (section.querySelector('.map-grid')) {
      ensureSourcePreview(kind, Store.getState().importState[kind]);
    }

    // 簡易清單的「預覽來源資料」：開資料預覽看來源原貌（grid 模式無此鈕）。
    var previewSourceBtn = section.querySelector('[data-action="preview-source-' + kind + '"]');
    if (previewSourceBtn && Ui.openDataPreview) {
      previewSourceBtn.addEventListener('click', function () {
        Ui.openDataPreview(kind === 'gl' ? 'glStaging' : 'tbStaging');
      });
    }

    var suggestBtn = section.querySelector('[data-action="suggest-' + kind + '"]');
    if (suggestBtn) {
      suggestBtn.addEventListener('click', function () {
        var importInfo = Store.getState().importState[kind];
        if (!importInfo) { return; }

        var fieldDefs = fields
          .filter(function (f) { return !f.literal; })
          .map(function (f) { return { key: f.key, label: f.label }; });

        Ui.run('自動建議', function () {
          return global.JetApi.mappingAutoSuggest({
            fields: fieldDefs,
            columns: importInfo.columns
          }).then(function (data) {
            var merged = Object.assign({}, Store.getState().mapping[kind].draft, data.suggested || {});
            Store.replaceMappingDraft(kind, merged);
            Store.addMessage('已套用自動建議（' +
              Object.keys(data.suggested || {}).length + ' 個欄位）。', 'info');
          });
        });
      });
    }

    var commitBtn = section.querySelector('[data-action="commit-' + kind + '"]');
    if (commitBtn) {
      commitBtn.addEventListener('click', function () {
        var current = Store.getState().mapping[kind];
        var mapping = {};
        Object.keys(current.draft).forEach(function (key) {
          if (current.draft[key]) { mapping[key] = current.draft[key]; }
        });

        var label = kind === 'gl' ? 'GL 配對' : 'TB 配對';
        var mode = kind === 'gl' ? current.amountMode : current.changeMode;

        Ui.run('提交' + label, function () {
          var startedAt = Date.now();
          var promise = kind === 'gl'
            ? global.JetApi.mappingCommitGl({ mapping: mapping, amountMode: mode })
            : global.JetApi.mappingCommitTb({ mapping: mapping, changeMode: mode });

          return promise.then(function (data) {
            editing[kind] = false;
            Store.setMappingCommitted(kind, {
              projectedRowCount: data.projectedRowCount,
              committedUtc: new Date().toISOString(),
              mapping: mapping,
              mode: mode
            });
            Store.addMessage(
              label + '已提交，已標準化 ' + Number(data.projectedRowCount).toLocaleString() +
              ' 列，耗時 ' + ((Date.now() - startedAt) / 1000).toFixed(1) + ' 秒。', 'info');
            // 後端非阻斷提醒（如必填欄整欄空白、疑似配錯欄）：逐則以警示色呈現，使用者立即看得到。
            (data.warnings || []).forEach(function (w) {
              Store.addMessage(label + '提醒：' + w, 'warn');
            });
          });
        });
      });
    }
  }

  /* ---- MockDataLoader -------------------------------------------------------- */

  function mockCommitMappings() {
    var state = Store.getState();
    if (!state.importState.gl || !state.importState.tb) {
      Store.addMessage('請先完成 GL 與 TB 匯入（可用「匯入資料」步驟的測試案件按鈕）。', 'warn');
      return;
    }

    Ui.run('套用測試案件（配對）', function () {
      var demo;
      return global.JetApi.projectLoadDemo({}).then(function (d) {
        demo = d;
        Store.replaceMappingDraft('gl', Object.assign({}, demo.gl.mapping));
        Store.setMappingMode('gl', demo.gl.amountMode);
        return global.JetApi.mappingCommitGl({
          mapping: demo.gl.mapping,
          amountMode: demo.gl.amountMode
        });
      }).then(function (glCommit) {
        editing.gl = false;
        Store.setMappingCommitted('gl', {
          projectedRowCount: glCommit.projectedRowCount,
          committedUtc: new Date().toISOString(),
          mapping: Object.assign({}, demo.gl.mapping),
          mode: demo.gl.amountMode
        });
        Store.replaceMappingDraft('tb', Object.assign({}, demo.tb.mapping));
        Store.setMappingMode('tb', demo.tb.changeMode);
        return global.JetApi.mappingCommitTb({
          mapping: demo.tb.mapping,
          changeMode: demo.tb.changeMode
        });
      }).then(function (tbCommit) {
        editing.tb = false;
        Store.setMappingCommitted('tb', {
          projectedRowCount: tbCommit.projectedRowCount,
          committedUtc: new Date().toISOString(),
          mapping: Object.assign({}, demo.tb.mapping),
          mode: demo.tb.changeMode
        });
        Store.addMessage('測試配對已提交（flag / debitCredit 模式），已標準化 GL ' +
          Store.getState().mapping.gl.committed.projectedRowCount + ' 列、TB ' +
          tbCommit.projectedRowCount + ' 列。', 'info');
      });
    });
  }

  Ui.registerStep('mapping', render);
  Ui.registerMockLoader('mapping', mockCommitMappings);
})(window);
