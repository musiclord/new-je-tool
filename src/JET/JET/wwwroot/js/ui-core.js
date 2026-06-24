/*
  前端共用核心（JetUi namespace）。
  持有：DOM 輔助、run() 包裝、流程閘門與導航、統一步驟頁尾、
  測試案件按鈕骨架、步驟渲染器與 mock loader 的註冊表、共用契約鏡像常數。
  各步驟渲染器以獨立檔案自行註冊（js/steps/*.js）；本檔不承載任何權威業務規則。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;

  /* ---- 共用契約鏡像（key/label 對齊 docs/action-contract-manifest.md） ------ */

  // GL 邏輯欄位。dcDebitCode 是借方代碼字面值（文字輸入），不是來源欄位 select。
  var GL_FIELDS = [
    { key: 'docNum', label: '傳票號碼', req: 'always' },
    { key: 'lineID', label: '傳票文件項次', req: 'optional' },
    { key: 'postDate', label: '總帳日期', req: 'always' },
    { key: 'docDate', label: '傳票核准日', req: 'optional' },
    { key: 'accNum', label: '會計科目編號', req: 'always' },
    { key: 'accName', label: '會計科目名稱', req: 'always' },
    { key: 'description', label: '傳票摘要', req: 'always' },
    { key: 'jeSource', label: '分錄來源模組', req: 'optional' },
    { key: 'createBy', label: '傳票建立人員', req: 'optional' },
    { key: 'approveBy', label: '傳票核准人員', req: 'optional' },
    { key: 'manual', label: '人工/自動分錄', req: 'optional' },
    { key: 'amount', label: '傳票金額（單欄）', req: ['signed', 'side', 'flag'] },
    { key: 'debitAmount', label: '借方金額', req: ['dual'] },
    { key: 'creditAmount', label: '貸方金額', req: ['dual'] },
    { key: 'dcField', label: '借貸別欄位', req: ['side', 'flag'] },
    { key: 'dcDebitCode', label: '借方標識代碼', req: ['side', 'flag'], literal: true }
  ];

  var TB_FIELDS = [
    { key: 'accNum', label: '會計科目編號', req: 'always' },
    { key: 'accName', label: '會計科目名稱', req: 'always' },
    { key: 'amount', label: '年度變動金額', req: ['direct'] },
    { key: 'debitAmt', label: '借方金額', req: ['debitCredit'] },
    { key: 'creditAmt', label: '貸方金額', req: ['debitCredit'] }
  ];

  var GL_MODES = [
    { value: 'dual', label: '借方欄＋貸方欄' },
    { value: 'signed', label: '單一帶號金額欄' },
    { value: 'side', label: '金額＋借貸別（文字）' },
    { value: 'flag', label: '金額＋借方旗標（數字）' }
  ];

  var TB_MODES = [
    { value: 'debitCredit', label: '借方欄−貸方欄' },
    { value: 'direct', label: '直接變動金額欄' }
  ];

  // 進階篩選的條件型別與顯示標籤。requiresAccountMapping 的型別在
  // 科目配對未匯入時不提供快速加入鈕（鏡像閘門，權威驗證仍在後端）。
  // group：依「審計員想問什麼」分組（FILTER_RULE_GROUPS）；UI 用它把快速加入鈕與型別下拉
  // 分塊呈現（NN/g chunking）。value 是 AST 型別鍵（wire 契約），不隨標籤調整改動。
  // quickLabel：快速加入鈕的字（省略時用 label）；label 用於型別下拉。
  var FILTER_RULE_TYPES = [
    { value: 'prescreen', label: '預篩選', quickLabel: '選擇風險訊號', group: 'risk' },
    { value: 'text', label: '文字條件', group: 'field' },
    { value: 'numRange', label: '金額區間', group: 'field' },
    { value: 'dateRange', label: '日期區間', group: 'field' },
    { value: 'customKeywords', label: '自訂關鍵字', group: 'field' },
    { value: 'periodInOut', label: '期內/期外', group: 'nature' },
    { value: 'drCrOnly', label: '借貸限定', group: 'nature' },
    { value: 'manualAuto', label: '人工/自動', group: 'nature' },
    { value: 'customTrailingZeros', label: '自訂尾數位數', group: 'pattern' },
    { value: 'accountPair', label: '科目配對分析', group: 'pattern', requiresAccountMapping: true },
    { value: 'specialAccountCategoryPair', label: '考量特殊科目類別配對', group: 'pattern', requiresAccountMapping: true },
    { value: 'customPreparerEntryCount', label: '自訂編製人員張數', group: 'pattern' },
    { value: 'customAccountEntryCount', label: '自訂科目張數', group: 'pattern' },
    // KCT 小組條件（清單 A/C/D/H/J；分組獨立於其他四組）。requiresAccountMapping 者需科目配對已匯入。
    { value: 'revenueDebitNearQuarterEnd', label: '季末前借記收入', group: 'kct', requiresAccountMapping: true },
    { value: 'revenueWithoutNormalCounterpart', label: '收入無一般對方科目', group: 'kct', requiresAccountMapping: true },
    { value: 'manualRevenueEntry', label: '收入之人工分錄', group: 'kct', requiresAccountMapping: true },
    { value: 'trailingDigits', label: '特定金額尾數', group: 'kct' },
    { value: 'preparerEqualsApprover', label: '編製與核准同一人', group: 'kct' }
  ];

  // 條件型別的分組（依審計意圖，非資料格式）。顯示順序即此陣列順序；
  // 每組約 3–4 項，讓使用者只看自己要的那一塊（NN/g chunking / progressive disclosure）。
  // kct 仍保留在此：它是 rule-row 型別下拉的 optgroup（讓 KCT 面板帶入的條件仍可在 query-builder
  // 內檢視/改型別）；但「快速加入」面板不再渲染 kct 分組（KCT 改由獨立的 FILTER_KCT_CHECKLIST
  // 面板進入，單一入口），詳見 filter-step.customPickerHtml。
  var FILTER_RULE_GROUPS = [
    { key: 'risk', label: '風險預篩選訊號' },
    { key: 'field', label: '依欄位內容' },
    { key: 'nature', label: '依分錄性質' },
    { key: 'pattern', label: '進階樣態分析' },
    { key: 'kct', label: 'KCT條件' }
  ];

  // KCT 小組「重用既有型別」的預設條件（清單 E/F/G/I）：點按鈕即帶入既有型別的預填規則，
  // 不另立 wire 型別（避免重複既有述詞，單一事實在後端述詞）。overrides 套在 newFilterRule 之上；
  // newGroup 為「整組帶入」（非營業日 = weekendPosting OR holidayPosting，自成一組以免 OR 結合錯位）。
  var FILTER_KCT_PRESETS = [
    { key: 'kctSpecificPreparer', label: '特定人員建立之分錄',
      overrides: { type: 'text', field: 'createBy', mode: 'exact' } },
    { key: 'kctSpecificKeywords', label: '特定摘要',
      overrides: { type: 'customKeywords' } },
    { key: 'kctBlankDescription', label: '空白摘要',
      overrides: { type: 'prescreen', prescreenKey: 'blankDescription' } },
    { key: 'kctNonBusinessDay', label: '非營業日分錄',
      newGroup: [
        { type: 'prescreen', prescreenKey: 'weekendPosting' },
        { type: 'prescreen', prescreenKey: 'holidayPosting', join: 'OR' }
      ] }
  ];

  // 預設群組（newGroup）在彙總區「扁平檢視」呈現為單一原子條件時的白話標籤（以卡的 ref＝preset key 為鍵）。
  // 目前僅非營業日(I) 為 newGroup（週末 OR 假日）；其餘預設為單規則、不走原子行。
  var FILTER_KCT_ATOM_LABELS = {
    kctNonBusinessDay: '非營業日（週末或假日）'
  };

  // KCT 小組方法學檢核清單（A–J）：獨立顯著面板的「單一資料來源」，十顆按鈕由此一份資料驅動，
  // 而非十段重複 HTML（Linus：讓分支消失而非加 if）。每筆只是「指向既有述詞」的標記：
  //   kind:'type'   → ref 是既有 FILTER_RULE_TYPES 的 value；點按帶入一條 newFilterRule(ref)。
  //   kind:'preset' → ref 是既有 FILTER_KCT_PRESETS 的 key；點按沿用該預設（含 newGroup 整組帶入）。
  //   disabled:true → Phase 2 佔位（B：待 KCT 交付 BS/IS 分類表），只渲染為停用，不實作述詞。
  // label 為 KCT 清單用語（卡片顯示文字）。每張卡是可複選 toggle：選取即把其規格落地成 rule
  // 併入草稿、並在每條 rule 打上 __kctLetter 身分標記（UI-only，剝除後才送 wire）；取消即移除帶
  // 該字母標記的 rule。情境名稱／動機一律必填，無任何 source 特例（已退場 source:'kct' 模型）。
  // 不在此引入任何新 wire 型別／述詞；A/C/D/H/J 與 E/F/G/I 全部重用既有 type/preset。
  var FILTER_KCT_CHECKLIST = [
    { letter: 'A', kind: 'type', ref: 'revenueDebitNearQuarterEnd', label: '在該季度前 X 天借記收入的會計分錄' },
    { letter: 'B', kind: 'type', ref: null, disabled: true, note: 'Phase 2 · 待 BS/IS',
      label: '借記固定資產(PPE，不含在建)且貸記費用之分錄' },
    { letter: 'C', kind: 'type', ref: 'revenueWithoutNormalCounterpart', label: '貸方為收入但借方非一般對方科目之分錄' },
    { letter: 'D', kind: 'type', ref: 'manualRevenueEntry', label: '收入之人工分錄' },
    { letter: 'E', kind: 'preset', ref: 'kctSpecificPreparer', label: '特定人員(財務長/執行長/高階主管等)建立之分錄' },
    { letter: 'F', kind: 'preset', ref: 'kctSpecificKeywords', label: '特定摘要(如迴轉、調整等)' },
    { letter: 'G', kind: 'preset', ref: 'kctBlankDescription', label: '空白摘要' },
    { letter: 'H', kind: 'type', ref: 'trailingDigits', label: '特定尾數(如 999999/000000 結尾)' },
    { letter: 'I', kind: 'preset', ref: 'kctNonBusinessDay', label: '非營業日之分錄' },
    { letter: 'J', kind: 'type', ref: 'preparerEqualsApprover', label: '編製人員與核准人員相同' }
  ];

  // 預篩選 row-tag 鍵與中文名（guide §4 命名登錄表；代號已退役，一律用具體名稱）。
  var PRESCREEN_KEY_OPTIONS = [
    { value: 'postPeriodApproval', label: '期末後核准' },
    { value: 'suspiciousKeywords', label: '摘要特定描述' },
    { value: 'unexpectedAccountPair', label: '未預期借貸組合', requiresAccountMapping: true },
    { value: 'trailingZeros', label: '連續零尾數金額' },
    { value: 'weekendPosting', label: '週末過帳' },
    { value: 'weekendApproval', label: '週末核准' },
    { value: 'holidayPosting', label: '假日過帳' },
    { value: 'holidayApproval', label: '假日核准' },
    { value: 'blankDescription', label: '摘要空白' },
    { value: 'nonAuthorizedPreparer', label: '非授權編製人員', requiresAuthorizedPreparers: true },
    { value: 'lowFrequencyPreparer', label: '低頻編製者' },
    { value: 'lowFrequencyAccount', label: '低頻科目' }
  ];

  // 科目配對分析的標準化分類選項（guide §2.3 白名單）。
  var ACCOUNT_CATEGORY_OPTIONS = [
    { value: 'Revenue', label: '收入（Revenue）' },
    { value: 'Receivables', label: '應收款項（Receivables）' },
    { value: 'Cash', label: '現金（Cash）' },
    { value: 'Receipt in advance', label: '預收款項（Receipt in advance）' },
    { value: 'Others', label: '其他（Others）' }
  ];

  // 科目配對分析三模式（guide §6.1）。
  var ACCOUNT_PAIR_MODE_OPTIONS = [
    { value: 'exact', label: '精確配對（借＋貸同傳票）' },
    { value: 'debitAnchor', label: '借方錨定（看對方科目）' },
    { value: 'creditAnchor', label: '貸方錨定（看對方科目）' }
  ];

  // 特殊科目類別配對三模式（manifest specialAccountCategoryPair；A=借方類別、B=貸方類別）。
  // 三模式皆需 A 與 B 皆填（否定模式同樣需要 B/A 才能判定「不存在」），與 accountPair 的
  // 錨定模式不同——故各 case 一律呈現借/貸兩個 categorySelect。標籤明確標示 Dr/Cr 與否定語意。
  var SPECIAL_PAIR_MODE_OPTIONS = [
    { value: 'drAndCr', label: 'Dr A、Cr B（借A貸B）' },
    { value: 'drNotCr', label: 'Dr A、Cr 非 B（借A、貸方無B）' },
    { value: 'notDrCr', label: 'Dr 非 A、Cr B（貸B、借方無A）' }
  ];

  var TEXT_MODE_OPTIONS = [
    { value: 'contains', label: '包含' },
    { value: 'exact', label: '完全符合' },
    { value: 'notContains', label: '不包含（排除）' },
    { value: 'notExact', label: '不等於（排除）' }
  ];

  // 可作文字／日期條件的邏輯欄位（白名單的前端鏡像；權威驗證在後端）。
  var FILTER_TEXT_FIELDS = ['docNum', 'lineID', 'accNum', 'accName', 'description', 'jeSource', 'createBy', 'approveBy'];
  var FILTER_DATE_FIELDS = ['postDate', 'docDate'];

  /* ---- DOM 輔助 ------------------------------------------------------------ */

  function $(bind) {
    return document.querySelector('[data-bind="' + bind + '"]');
  }

  function setText(bind, text) {
    var el = $(bind);
    if (el) { el.textContent = text; }
  }

  function esc(text) {
    var div = document.createElement('div');
    div.textContent = text == null ? '' : String(text);
    return div.innerHTML;
  }

  /* ---- 全域欄位驗證提示（就近顯示在欄位旁，不依賴可收合的訊息欄） -------------
     依 NN/g「錯誤訊息就近於欄位、用多重線索（紅框＋紅字）、別只靠顏色」。
     這是純呈現輔助：標紅框、在欄位（form__row）下方插一行紅字；權威驗證仍在後端。
     任何步驟的必填欄位都可重用，毋須各步驟自行刻一套。 */

  // 在欄位上加紅框並在其所在 form__row 下方顯示一行訊息。message 省略時沿用既有文字。
  function setFieldError(input, message) {
    if (!input) { return; }
    input.classList.add('form__input--error');
    input.setAttribute('aria-invalid', 'true');
    var row = input.parentNode;
    if (!row) { return; }
    var err = row.querySelector('.field-error');
    if (!err) {
      err = document.createElement('span');
      err.className = 'field-error';
      err.setAttribute('role', 'alert');
      row.appendChild(err);
    }
    if (message != null) { err.textContent = message; }
  }

  // 清掉某欄位的錯誤狀態（紅框與訊息）。使用者修正欄位時即呼叫，避免「持續責備」。
  function clearFieldError(input) {
    if (!input) { return; }
    input.classList.remove('form__input--error');
    input.removeAttribute('aria-invalid');
    var row = input.parentNode;
    var err = row ? row.querySelector('.field-error') : null;
    if (err && err.parentNode) { err.parentNode.removeChild(err); }
  }

  // 統一的 action 執行包裝：busy 狀態 + 錯誤導入訊息區。
  // 錯誤在此吸收（已顯示給使用者），避免 unhandled rejection。
  function run(label, promiseFactory) {
    Store.setBusy(true);
    return promiseFactory()
      .catch(function (error) {
        Store.addMessage(label + '失敗：' + error.message, 'warn');
      })
      .finally(function () {
        Store.setBusy(false);
      });
  }

  function isRequired(field, mode) {
    if (field.req === 'always') { return true; }
    if (Array.isArray(field.req)) { return field.req.indexOf(mode) >= 0; }
    return false;
  }

  function glFieldLabel(key) {
    var hit = GL_FIELDS.filter(function (f) { return f.key === key; })[0];
    return hit ? hit.label : key;
  }

  /* ---- 步驟渲染器 / mock loader / workflow 重設 的註冊表 -------------------- */

  var stepRenderers = {};
  var mockLoaders = {};
  var workflowResets = [];

  function registerStep(stepId, renderer) {
    stepRenderers[stepId] = renderer;
  }

  function registerMockLoader(stepId, loader) {
    mockLoaders[stepId] = loader;
  }

  // 步驟模組的區域狀態（如待匯入清單）在離開專案／載入專案時歸零。
  function registerWorkflowReset(fn) {
    workflowResets.push(fn);
  }

  function resetStepModules() {
    workflowResets.forEach(function (fn) { fn(); });
  }

  function renderStep(stepId, container, state) {
    var renderer = stepRenderers[stepId];
    if (renderer) { renderer(container, state); }
  }

  /* ---- 流程閘門（state-oriented：每一步的進入條件與缺漏） ------------------- */

  // 步驟 index 的進入條件；missing 描述「還缺什麼」。
  // 對齊 docs/jet-frontend-description.md §10 閘門模型（6 步版本）。
  function stepGate(state, index) {
    var missing = [];
    switch (index) {
      case 1:
        if (!state.project) { missing.push('建立或載入案件'); }
        break;
      case 2:
        if (!state.importState.gl) { missing.push('匯入 GL 檔案'); }
        if (!state.importState.tb) { missing.push('匯入 TB 檔案'); }
        break;
      case 3:
        if (!state.mapping.gl.committed) { missing.push('確認 GL 欄位配對'); }
        if (!state.mapping.tb.committed) { missing.push('確認 TB 欄位配對'); }
        break;
      case 4:
        if (!state.lastRuns.validate) { missing.push('執行資料驗證'); }
        break;
      case 5:
        if (state.filter.savedScenarios.length === 0) { missing.push('保存至少一個篩選情境'); }
        break;
    }
    return { ok: missing.length === 0, missing: missing };
  }

  // 線性流程：進入 index 必須滿足沿路所有閘門。
  function isStepReachable(state, index) {
    for (var i = 1; i <= index; i++) {
      if (!stepGate(state, i).ok) { return false; }
    }
    return true;
  }

  // 鎖定步驟的 tooltip：沿路所有缺漏條件（去重）。
  function lockedStepTip(state, index) {
    var all = [];
    for (var i = 1; i <= index; i++) {
      stepGate(state, i).missing.forEach(function (item) {
        if (all.indexOf(item) < 0) { all.push(item); }
      });
    }
    return all.length ? '需先完成：' + all.join('、') : '';
  }

  // 統一導航入口：切換步驟並把目前位置持久化到專案（resume 用）。
  function gotoStep(index) {
    var state = Store.getState();
    if (index < 0 || index >= Store.STEPS.length || !isStepReachable(state, index)) {
      return;
    }
    Store.setStepIndex(index);
    persistProgress(index);
  }

  // fire-and-forget：進度保存失敗不阻斷操作，只提示。
  function persistProgress(index) {
    var state = Store.getState();
    if (!state.project || !global.JetApi.isReady()) { return; }
    global.JetApi.projectSaveProgress({ currentStep: index }).catch(function (error) {
      Store.addMessage('進度保存失敗：' + error.message, 'warn');
    });
  }

  // 儲存並結束：先持久化目前位置，再請 host 關閉視窗。
  // 視窗可能在 response 抵達前關閉，因此不依賴 host.exitApp 的回應。
  function exitApp() {
    if (!global.JetApi.isReady()) {
      Store.addMessage('Host 未連線，請直接關閉視窗。', 'warn');
      return;
    }

    var state = Store.getState();
    var save = (state.view === 'workflow' && state.project)
      ? global.JetApi.projectSaveProgress({ currentStep: state.currentStepIndex })
          .catch(function (error) {
            // 每次切換步驟時都已保存過進度，這裡保存失敗仍允許結束。
            Store.addMessage('進度保存失敗：' + error.message, 'warn');
          })
      : Promise.resolve();

    Store.setBusy(true);
    save.then(function () {
      return global.JetApi.hostExitApp({});
    }).catch(function (error) {
      Store.setBusy(false);
      Store.addMessage('結束應用程式失敗：' + error.message, 'warn');
    });
  }

  /* ---- 專案載入 / 切換 ------------------------------------------------------ */

  function loadProjects() {
    return run('載入專案清單', function () {
      return global.JetApi.projectList({}).then(function (data) {
        Store.setProjects(data.projects || []);
      });
    });
  }

  // 統一的「回專案選擇」：清空 workflow 與步驟模組狀態再切回 picker。
  function goBackToPicker() {
    resetStepModules();
    Store.resetWorkflow();
    Store.setView('picker');
    loadProjects();
  }

  // 頁首「回專案選擇」：比照 exitApp 先保存目前步驟位置，再切回 picker（保存失敗仍允許切回）。
  function backToPickerFromHeader() {
    var state = Store.getState();
    if (state.view === 'workflow' && state.project && global.JetApi.isReady()) {
      global.JetApi.projectSaveProgress({ currentStep: state.currentStepIndex })
        .catch(function (error) {
          Store.addMessage('進度保存失敗：' + error.message, 'warn');
        })
        .finally(goBackToPicker);
      return;
    }
    goBackToPicker();
  }

  function openProject(projectId) {
    return run('載入專案', function () {
      return global.JetApi.projectLoad({ projectId: projectId }).then(function (data) {
        applyLoadedProject(data);

        // 還原持久化的訊息歷史（log.recent）；失敗不影響專案載入
        return global.JetApi.logRecent({}).then(function (log) {
          Store.seedMessages(log.messages || []);
        }).catch(function () {}).then(function () {
          Store.addMessage('已載入專案「' + data.project.entityName + '」。', 'info');
        });
      });
    });
  }

  function applyLoadedProject(data, options) {
    Store.setProject(data.project);

    Store.setImportResult('gl', data.importState ? data.importState.gl : null);
    Store.setImportResult('tb', data.importState ? data.importState.tb : null);
    Store.setAccountMappingState(data.importState ? data.importState.accountMapping : null);
    Store.setAuthorizedPreparerState(data.importState ? data.importState.authorizedPreparer : null);
    Store.setCalendarState(data.importState ? data.importState.calendar : null);
    resetStepModules();

    if (data.mapping && data.mapping.gl) {
      Store.replaceMappingDraft('gl', data.mapping.gl.mapping || {});
      Store.setMappingMode('gl', data.mapping.gl.amountMode || 'dual');
      // 完整快照供配對步驟判定「草稿是否偏離已提交版本」；resume 無標準化列數（null）。
      Store.setMappingCommitted('gl', {
        committedUtc: data.mapping.gl.committedUtc,
        mapping: data.mapping.gl.mapping || {},
        mode: data.mapping.gl.amountMode || 'dual',
        projectedRowCount: null
      });
    } else {
      Store.replaceMappingDraft('gl', {});
      Store.setMappingCommitted('gl', null);
    }

    if (data.mapping && data.mapping.tb) {
      Store.replaceMappingDraft('tb', data.mapping.tb.mapping || {});
      Store.setMappingMode('tb', data.mapping.tb.changeMode || 'debitCredit');
      Store.setMappingCommitted('tb', {
        committedUtc: data.mapping.tb.committedUtc,
        mapping: data.mapping.tb.mapping || {},
        mode: data.mapping.tb.changeMode || 'debitCredit',
        projectedRowCount: null
      });
    } else {
      Store.replaceMappingDraft('tb', {});
      Store.setMappingCommitted('tb', null);
    }

    // 規則執行結果與已保存情境的 resume（原樣回放後端 latestRuns / filterScenarios）。
    Store.setLastRun('validate', data.latestRuns ? data.latestRuns.validate : null);
    Store.setLastRun('prescreen', data.latestRuns ? data.latestRuns.prescreen : null);
    Store.setSavedScenarios(data.filterScenarios || []);
    Store.setFilterDraft(null); // 重設為空草稿（避免殘留上一個案件的條件）

    Store.setView('workflow');

    // 「套用測試案件（建立）」要求套用狀態但停在目前步驟，不自動前進到匯入。
    if (options && options.stayOnCurrentStep) {
      return;
    }

    // resume 到上次保存的位置；0 也是合法位置（建立案件步驟）。
    var saved = typeof data.project.currentStep === 'number' ? data.project.currentStep : 1;
    var stepIndex = Math.min(Math.max(saved, 0), Store.STEPS.length - 1);

    // 防呆：保存位置若超出閘門允許範圍（如資料被清除），退回最近可達的步驟。
    while (stepIndex > 0 && !isStepReachable(Store.getState(), stepIndex)) {
      stepIndex--;
    }

    Store.setStepIndex(stepIndex);
  }

  /* ---- 統一步驟頁尾（workflow-centric：每一步同位置、同語彙的前進導引） ------ */

  function stepFooterHtml(state) {
    var steps = Store.STEPS;
    var index = state.currentStepIndex;
    var isLast = index === steps.length - 1;

    var statusHtml;
    var nextBtn = '';
    if (isLast) {
      statusHtml =
        '<p class="step-footer__hint">這是最後一步：匯出完成後，可按右上角「儲存並結束」關閉並保留進度。</p>';
    } else {
      var nextLabel = steps[index + 1].label;
      var gate = stepGate(state, index + 1);
      statusHtml = gate.ok
        ? '<p class="step-footer__hint step-footer__hint--ok">本步驟條件已備齊，可前往「' + nextLabel + '」。</p>'
        : '<p class="step-footer__hint">前往「' + nextLabel + '」前需要：' + gate.missing.join('、') + '。</p>';
      nextBtn = '<button type="button" class="btn" data-action="step-next"' + (gate.ok ? '' : ' disabled') +
        '>下一步：' + nextLabel + '</button>';
    }

    var prevBtn = index === 0
      ? ''
      : '<button type="button" class="btn btn--ghost" data-action="step-prev">上一步：' +
        steps[index - 1].label + '</button>';

    return (
      '<footer class="step-footer">' +
        statusHtml +
        '<div class="step-footer__nav">' + prevBtn + nextBtn + '</div>' +
      '</footer>'
    );
  }

  function bindStepFooter(container) {
    var prev = container.querySelector('[data-action="step-prev"]');
    if (prev) {
      prev.addEventListener('click', function () {
        gotoStep(Store.getState().currentStepIndex - 1);
      });
    }

    var next = container.querySelector('[data-action="step-next"]');
    if (next) {
      next.addEventListener('click', function () {
        gotoStep(Store.getState().currentStepIndex + 1);
      });
    }

  }

  /* ---- MockDataLoader 按鈕骨架（dev 輔助；各步驟的 loader 自行註冊） --------- */

  var MOCK_BUTTON_HTML =
    '<button type="button" class="btn btn--ghost btn--mock" data-action="mock-load">套用測試案件</button>';

  function bindMockButton(container, stepId) {
    var btn = container.querySelector('[data-action="mock-load"]');
    if (!btn) { return; }

    btn.addEventListener('click', function () {
      var loader = mockLoaders[stepId];
      if (loader) {
        loader();
        return;
      }

      var step = Store.STEPS.filter(function (s) { return s.id === stepId; })[0];
      Store.addMessage(
        '「' + (step ? step.label : stepId) + '」流程尚未實作；MockDataLoader 載入點已預留。', 'info');
    });
  }

  /* ---- 共用 helper：預覽表格與篩選規則預設值 --------------------------------- */

  // 把 previewRows 陣列轉成 <table class="preview-table"> 的完整標記。
  // 欄位順序：傳票號碼、項次、總帳日期、科目（代碼＋名稱）、摘要、金額、借貸。
  // 供 filter-step 與 validate-step 共用；呼叫端自行包 <div class="preview-table__wrap">。
  function previewTableHtml(previewRows) {
    var rows = (previewRows || []).map(function (r) {
      return (
        '<tr>' +
          '<td>' + esc(r.documentNumber) + '</td>' +
          '<td>' + esc(r.lineItem || '—') + '</td>' +
          '<td>' + esc(r.postDate) + '</td>' +
          '<td>' + esc(r.accountCode) + ' ' + esc(r.accountName || '') + '</td>' +
          '<td>' + esc(r.documentDescription || '') + '</td>' +
          '<td class="preview-table__amount">' + Number(r.amount).toLocaleString() + '</td>' +
          '<td>' + (r.drCr === 'DEBIT' ? '借' : '貸') + '</td>' +
        '</tr>'
      );
    }).join('');

    return (
      '<table class="preview-table">' +
        '<thead><tr><th>傳票號碼</th><th>項次</th><th>總帳日期</th><th>科目</th><th>摘要</th>' +
          '<th>金額</th><th>借貸</th></tr></thead>' +
        '<tbody>' + rows + '</tbody>' +
      '</table>'
    );
  }

  // 建立一條篩選規則的預設值物件（純資料，無 Store/Ui 依賴）。
  // 供 filter-step 的「快速加入」與型別切換，以及 validate-step 的預篩選預覽 scenario builder 共用。
  function newFilterRule(type) {
    return {
      join: 'AND',
      type: type,
      prescreenKey: 'suspiciousKeywords',
      field: type === 'dateRange' ? 'postDate' : (type === 'numRange' ? 'amount' : 'description'),
      // 只有 trailingDigits（KCT 條件 H · 特定金額尾數）有預設值：H 要求選取後即帶 000000；
      // 其餘型別無「預設尾數」語意，故維持空字串由使用者自填。
      keywords: type === 'trailingDigits' ? '000000' : '',
      mode: 'contains',
      from: '',
      to: '',
      drCr: 'debit',
      isManual: 'true',
      // pairMode 的合法取值依型別而異：accountPair 為 exact/debitAnchor/creditAnchor，
      // specialAccountCategoryPair 為 drAndCr/drNotCr/notDrCr。型別切換經此重建（filter-step
      // 的 type-change handler），故各型別取自己模式集的預設，不會殘留另一型別的 pairMode。
      pairMode: type === 'specialAccountCategoryPair' ? 'drAndCr' : 'exact',
      debitCategory: 'Receivables',
      creditCategory: 'Revenue',
      inPeriod: 'true',
      digits: '3',
      maxEntries: (type === 'customPreparerEntryCount' || type === 'customAccountEntryCount') ? '11' : '',
      windowDays: ''
    };
  }

  /* ---- 共用：「載入更多」keyset 接列膠水 ------------------------------------ */

  // 綁一顆「載入更多」鈕到 keyset 分頁:點擊 → 帶當前 cursor 呼叫 fetchPage(cursor)
  // → 後端回 { rows, nextCursor };appendRows(rows) 由呼叫端提供(把列接到既有 tbody);
  // cursor 以閉包保存累進;nextCursor 為 null 表已到底 → 移除鈕。
  // 純 DOM／呼叫膠水:不算差異、不判命中、不組 SQL;只發 action、接 data.rows、管 cursor 與載入態。
  // fetchPage 回傳 Promise<{ rows, nextCursor }>。
  //
  // 重要:cursor 一律自 null 起(keyset 第一頁,ASC)——不從鈕讀任何 seed。展開時上方的預覽列
  // 是另一套排序(ABS-DESC top-50 / filter 前 10),與 page 的 keyset ASC 不一致。因此「首擊」時
  // 先呼叫 clearTarget()(由呼叫端提供,清空目標 tbody 既有預覽列)再 append 第一頁:一旦點「載入
  // 更多」即由預覽切換為單一一致排序、無重複的完整列表。clearTarget 省略時不清(首屏即空表的情形)。
  function bindLoadMore(buttonEl, fetchPage, appendRows, clearTarget) {
    if (!buttonEl) { return; }
    var cursor = null;
    var cleared = false;
    buttonEl.addEventListener('click', function () {
      buttonEl.disabled = true;
      var prev = buttonEl.textContent;
      buttonEl.textContent = '載入中…';
      run('載入更多', function () {
        return fetchPage(cursor).then(function (data) {
          if (!cleared) {
            // 首擊:清掉上方預覽列一次,再接 page 第一頁。
            if (typeof clearTarget === 'function') { clearTarget(); }
            cleared = true;
          }
          appendRows((data && data.rows) || []);
          cursor = data ? data.nextCursor : null;
          if (cursor == null) {
            buttonEl.remove();
          } else {
            buttonEl.disabled = false;
            buttonEl.textContent = prev;
          }
        }).catch(function (error) {
          // 還原鈕態讓使用者可重試;錯誤交回 run() 顯示給使用者。
          buttonEl.disabled = false;
          buttonEl.textContent = prev;
          throw error;
        });
      });
    });
  }

  // 把後端回傳的列(物件陣列)依欄位鍵序轉成 <tr> 並接到 tbody。
  // columns:每欄一個取值函式 row→cell,或 { cell:fn, className } 帶 td class(沿用既有欄樣式);
  // 呼叫端決定欄序與顯示,沿用既有 table 欄序;一律 esc 後插入,前端不做任何業務轉換(顯示形狀由後端決定)。
  function appendRowsToTbody(tbody, rows, columns) {
    if (!tbody) { return; }
    var html = (rows || []).map(function (row) {
      return '<tr>' + columns.map(function (col) {
        var fn = typeof col === 'function' ? col : col.cell;
        var cls = (col && col.className) ? ' class="' + col.className + '"' : '';
        return '<td' + cls + '>' + esc(fn(row)) + '</td>';
      }).join('') + '</tr>';
    }).join('');
    tbody.insertAdjacentHTML('beforeend', html);
  }

  /* ---- 共用面板片段 ---------------------------------------------------------- */

  function noProjectPanel(title) {
    return (
      '<div class="panel">' +
        '<h2 class="panel__title">' + title + '</h2>' +
        '<p class="panel__hint">尚未建立或載入專案。</p>' +
        '<div class="panel__actions">' +
          '<button type="button" class="btn" data-action="back-picker">回專案選擇</button>' +
        '</div>' +
      '</div>'
    );
  }

  function bindNoProjectPanel(container) {
    container.querySelector('[data-action="back-picker"]').addEventListener('click', goBackToPicker);
  }

  global.JetUi = {
    // 契約鏡像常數
    GL_FIELDS: GL_FIELDS,
    TB_FIELDS: TB_FIELDS,
    GL_MODES: GL_MODES,
    TB_MODES: TB_MODES,
    FILTER_RULE_TYPES: FILTER_RULE_TYPES,
    FILTER_RULE_GROUPS: FILTER_RULE_GROUPS,
    FILTER_KCT_PRESETS: FILTER_KCT_PRESETS,
    FILTER_KCT_ATOM_LABELS: FILTER_KCT_ATOM_LABELS,
    FILTER_KCT_CHECKLIST: FILTER_KCT_CHECKLIST,
    PRESCREEN_KEY_OPTIONS: PRESCREEN_KEY_OPTIONS,
    ACCOUNT_CATEGORY_OPTIONS: ACCOUNT_CATEGORY_OPTIONS,
    ACCOUNT_PAIR_MODE_OPTIONS: ACCOUNT_PAIR_MODE_OPTIONS,
    SPECIAL_PAIR_MODE_OPTIONS: SPECIAL_PAIR_MODE_OPTIONS,
    TEXT_MODE_OPTIONS: TEXT_MODE_OPTIONS,
    FILTER_TEXT_FIELDS: FILTER_TEXT_FIELDS,
    FILTER_DATE_FIELDS: FILTER_DATE_FIELDS,
    // DOM / 執行輔助
    $: $,
    setText: setText,
    esc: esc,
    setFieldError: setFieldError,
    clearFieldError: clearFieldError,
    run: run,
    isRequired: isRequired,
    glFieldLabel: glFieldLabel,
    // 註冊表
    registerStep: registerStep,
    registerMockLoader: registerMockLoader,
    registerWorkflowReset: registerWorkflowReset,
    renderStep: renderStep,
    // 流程
    stepGate: stepGate,
    isStepReachable: isStepReachable,
    lockedStepTip: lockedStepTip,
    gotoStep: gotoStep,
    exitApp: exitApp,
    backToPickerFromHeader: backToPickerFromHeader,
    loadProjects: loadProjects,
    goBackToPicker: goBackToPicker,
    openProject: openProject,
    applyLoadedProject: applyLoadedProject,
    // 面板片段
    stepFooterHtml: stepFooterHtml,
    bindStepFooter: bindStepFooter,
    MOCK_BUTTON_HTML: MOCK_BUTTON_HTML,
    bindMockButton: bindMockButton,
    noProjectPanel: noProjectPanel,
    bindNoProjectPanel: bindNoProjectPanel,
    // 共用 helper
    previewTableHtml: previewTableHtml,
    newFilterRule: newFilterRule,
    bindLoadMore: bindLoadMore,
    appendRowsToTbody: appendRowsToTbody
  };
})(window);
