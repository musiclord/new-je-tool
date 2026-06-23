/*
  Frontend UI state boundary.
  Keep transient view state here; do not store GL/TB row populations.
*/
(function (global) {
  'use strict';

  // 流程階段定義（僅作前端導航與呈現用途，非後端契約）。
  // 6 步模型：驗證與預篩選合併於「資料驗證與測試」（manifest Step Data Outline；2026-06-10 定案）。
  var STEPS = [
    { id: 'create', label: '建立案件' },
    { id: 'import', label: '匯入資料' },
    { id: 'mapping', label: '欄位配對' },
    { id: 'validate', label: '資料驗證與測試' },
    { id: 'filter', label: '進階條件篩選' },
    { id: 'export', label: '匯出底稿' }
  ];

  // 僅保存輕量的 UI 狀態，不得保存完整 GL/TB row set。
  var state = {
    view: 'picker',            // 'picker' | 'workflow'
    currentStepIndex: 0,
    bridgeReady: false,
    devToolsEnabled: false,    // system.ping 回報；Release 組建為 false（開發面板隱藏）
    caseId: null,
    caseClient: null,
    messages: [],
    messagesPanelOpen: false,  // 「狀態與訊息」預設收合為右側窄欄
    messagesUnseen: 0,         // 收合期間新增的訊息數（展開時歸零）
    busy: false,
    projects: [],              // project.list 摘要
    project: null,             // 當前專案 metadata（project.load / create 回傳）
    importState: {
      gl: null,                // { batchId, rowCount, columns, fileName, sources: [...] }
      tb: null,
      accountMapping: null,    // { batchId, rowCount, fileName, importedUtc }
      authorizedPreparer: null, // 匯入當下 { batchId, rowCount, fileName, importedUtc }；resume(project.load) 只 { rowCount }
      calendar: null           // { holidayCount, makeupDayCount }
    },
    // committed = 已提交快照 { projectedRowCount, committedUtc, mapping, mode }（resume 時 projectedRowCount 可為 null）；
    // invalidatedByImport = 來源變更使配對失效（顯示說明橫幅，重新提交後解除）。
    mapping: {
      gl: { draft: {}, amountMode: 'dual', committed: null, invalidatedByImport: false },
      tb: { draft: {}, changeMode: 'debitCredit', committed: null, invalidatedByImport: false }
    },
    // 欄位配對介面偏好（session 級；不被 resetWorkflow 清除）。'classic'（預設三欄表）| 'grid'（二維表）
    mappingUiMode: 'classic',
    // 最近一次 validate.run / prescreen.run 的 response（resume 自 project.load.latestRuns）。
    lastRuns: { validate: null, prescreen: null },
    // 進階條件篩選：AST 草稿（Query Builder 本地組裝；恆為物件，空草稿 = groups 為空）、
    // 預覽結果、已保存情境。
    filter: { draft: { name: '', rationale: '', groups: [] }, preview: null, savedScenarios: [] },
    // 資料變動版本號：app.js 以此判斷是否需要重建中央面板。
    contentVersion: 0
  };

  var listeners = [];

  // 訊息流水號：供前端做增量渲染，辨識哪些訊息是新加入的。
  var messageSeq = 0;

  function notify() {
    for (var i = 0; i < listeners.length; i++) {
      listeners[i](state);
    }
  }

  function bump() {
    state.contentVersion++;
    notify();
  }

  var Store = {
    STEPS: STEPS,

    getState: function () {
      return state;
    },

    subscribe: function (fn) {
      if (typeof fn === 'function') {
        listeners.push(fn);
      }
    },

    setStepIndex: function (index) {
      if (index < 0 || index >= STEPS.length) {
        return;
      }
      state.currentStepIndex = index;
      notify();
    },

    setBridgeReady: function (ready) {
      state.bridgeReady = !!ready;
      notify();
    },

    setDevToolsEnabled: function (enabled) {
      state.devToolsEnabled = !!enabled;
      notify();
    },

    setView: function (view) {
      state.view = view;
      bump();
    },

    setBusy: function (busy) {
      state.busy = !!busy;
      notify();
    },

    setProjects: function (projects) {
      state.projects = projects || [];
      bump();
    },

    setProject: function (project) {
      state.project = project;
      state.caseId = project ? project.projectCode : null;
      state.caseClient = project ? project.entityName : null;
      bump();
    },

    // 離開專案（回 picker / 建立新案件）時清空 workflow 狀態，
    // 避免上一個案件的資料殘留在建立案件等步驟。
    resetWorkflow: function () {
      state.project = null;
      state.caseId = null;
      state.caseClient = null;
      state.importState = { gl: null, tb: null, accountMapping: null, authorizedPreparer: null, calendar: null };
      state.mapping = {
        gl: { draft: {}, amountMode: 'dual', committed: null, invalidatedByImport: false },
        tb: { draft: {}, changeMode: 'debitCredit', committed: null, invalidatedByImport: false }
      };
      state.lastRuns = { validate: null, prescreen: null };
      state.filter = { draft: { name: '', rationale: '', groups: [] }, preview: null, savedScenarios: [] };
      state.currentStepIndex = 0;
      bump();
    },

    setImportResult: function (kind, info) {
      // 匯入（replace 或 append）會使後端 committed mapping 失效，前端狀態同步歸零；
      // 原本已提交時標記失效原因（配對步驟顯示「來源資料已變更」橫幅）。
      state.mapping[kind].invalidatedByImport = !!state.mapping[kind].committed;
      state.importState[kind] = info;
      state.mapping[kind].committed = null;
      bump();
    },

    // 模組區域狀態（如匯入精靈的待匯入清單）變動時觸發面板重建。
    touch: function () {
      bump();
    },

    setCalendarState: function (info) {
      state.importState.calendar = info;
      bump();
    },

    // 科目配對（無欄位配對步驟，匯入即投影；不影響 GL/TB 配對狀態）。
    setAccountMappingState: function (info) {
      state.importState.accountMapping = info;
      bump();
    },

    // 授權編製人員清單（整份替換的設定檔，匯入即生效；不影響 GL/TB 配對狀態）。
    setAuthorizedPreparerState: function (info) {
      state.importState.authorizedPreparer = info;
      bump();
    },

    setLastRun: function (kind, summary) {
      state.lastRuns[kind] = summary || null;
      bump();
    },

    setFilterDraft: function (draft) {
      state.filter.draft = draft || { name: '', rationale: '', groups: [] };
      state.filter.preview = null; // 草稿結構變動使預覽失效
      bump();
    },

    // 規則值編輯（input/select）只 notify 不重建面板，避免輸入焦點被吃掉。
    patchFilterRule: function (groupIndex, ruleIndex, patch) {
      var draft = state.filter.draft;
      if (!draft.groups[groupIndex] || !draft.groups[groupIndex].rules[ruleIndex]) {
        return;
      }
      Object.assign(draft.groups[groupIndex].rules[ruleIndex], patch);
      notify();
    },

    patchFilterDraftMeta: function (patch) {
      Object.assign(state.filter.draft, patch);
      notify();
    },

    setFilterPreview: function (preview) {
      state.filter.preview = preview;
      bump();
    },

    setSavedScenarios: function (list) {
      state.filter.savedScenarios = list || [];
      bump();
    },

    setMappingDraft: function (kind, key, column) {
      if (column) {
        state.mapping[kind].draft[key] = column;
      } else {
        delete state.mapping[kind].draft[key];
      }
      notify(); // 草稿變動不重建面板（select 已是最新值）
    },

    // 對照表格用：把某來源欄指派給某 JET 欄位（空 fieldKey = 此欄不對應）。
    // 維持一對一：先解除任何已指向此欄的欄位，再指派；設 draft[fieldKey] 會自動覆蓋該欄位舊指向。
    assignColumnToField: function (kind, column, fieldKey, literalKeys) {
      var skip = literalKeys || [];
      var draft = state.mapping[kind].draft;
      Object.keys(draft).forEach(function (k) {
        if (skip.indexOf(k) < 0 && draft[k] === column) { delete draft[k]; }
      });
      if (fieldKey) { draft[fieldKey] = column; }
      bump(); // 指派會牽動其他標頭的選取狀態，需重建面板
    },

    replaceMappingDraft: function (kind, draft) {
      state.mapping[kind].draft = draft || {};
      bump();
    },

    setMappingMode: function (kind, mode) {
      if (kind === 'gl') {
        state.mapping.gl.amountMode = mode;
      } else {
        state.mapping.tb.changeMode = mode;
      }
      bump();
    },

    setMappingUiMode: function (uiMode) {
      state.mappingUiMode = uiMode === 'grid' ? 'grid' : 'classic';
      bump();
    },

    // 失效旗標只由 setImportResult 立起；任何明確的提交狀態設定（含 resume 的 null）都解除。
    setMappingCommitted: function (kind, result) {
      state.mapping[kind].committed = result;
      state.mapping[kind].invalidatedByImport = false;
      bump();
    },

    addMessage: function (text, level) {
      state.messages.unshift({
        id: ++messageSeq,
        text: text,
        level: level || 'info',
        time: new Date().toLocaleTimeString('zh-Hant', { hour12: false })
      });
      // 僅保留最近數則訊息，避免無上限累積（完整歷史持久化於專案資料庫 app_message_log）。
      state.messages = state.messages.slice(0, 30);

      if (!state.messagesPanelOpen) {
        state.messagesUnseen++;
      }

      notify();
    },

    toggleMessagesPanel: function () {
      state.messagesPanelOpen = !state.messagesPanelOpen;
      if (state.messagesPanelOpen) {
        state.messagesUnseen = 0;
      }
      notify();
    },

    // 以持久化歷史（log.recent，新→舊）取代目前訊息清單：project.load 後還原面板。
    // fromLog 標記讓持久化訂閱者不回寫（避免重複落庫）。
    // id 由舊到新遞增指派（增量渲染依賴「清單首位 id 最大」的不變量）。
    seedMessages: function (entries) {
      var source = (entries || []).slice(0, 30);
      var mapped = new Array(source.length);

      for (var i = source.length - 1; i >= 0; i--) {
        mapped[i] = {
          id: ++messageSeq,
          text: source[i].text,
          level: source[i].level || 'info',
          time: new Date(source[i].occurredUtc).toLocaleTimeString('zh-Hant', { hour12: false }),
          fromLog: true
        };
      }

      state.messages = mapped;
      state.messagesUnseen = 0;
      notify();
    }
  };

  global.JetStore = Store;
})(window);
