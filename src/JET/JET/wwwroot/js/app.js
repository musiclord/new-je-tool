/*
  前端殼層與啟動入口（bootstrap boundary）。
  持有：主渲染迴圈、專案選擇畫面、步驟導航、訊息區、啟動流程。
  共用核心在 js/ui-core.js（JetUi）；各步驟渲染器在 js/steps/*.js 自行註冊；
  本檔不承載任何權威業務規則。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  // 增量渲染狀態
  var stepButtons = null;
  var lastContentKey = null;
  var lastPickerKey = null;
  var lastMessageId = 0;
  var messagesEmptyShown = false;

  /* ---- 主渲染 ---------------------------------------------------------- */

  function render() {
    var state = Store.getState();
    var steps = Store.STEPS;
    var current = steps[state.currentStepIndex];

    document.body.classList.toggle('is-busy', state.busy);

    var picker = Ui.$('project-picker');
    var body = Ui.$('app-body');
    var dataPreview = Ui.$('data-preview');
    var devPanel = Ui.$('dev-panel');
    var devLogPanel = Ui.$('dev-log-panel');
    var backPicker = Ui.$('back-picker');

    if (state.view === 'picker') {
      if (picker) { picker.hidden = false; }
      if (body) { body.hidden = true; }
      if (dataPreview) { dataPreview.hidden = true; }
      if (devPanel) { devPanel.hidden = true; }
      if (devLogPanel) { devLogPanel.hidden = true; }
      if (backPicker) { backPicker.hidden = true; }
      renderSummary(state, current);
      renderPicker(state);
      renderMessages(state);
      return;
    }

    if (picker) { picker.hidden = true; }
    if (body) { body.hidden = false; }
    if (dataPreview) { dataPreview.hidden = false; }
    // 開發面板只在 Debug 組建顯示（system.ping.devToolsEnabled；Release 連 action 都未註冊）
    if (devPanel) { devPanel.hidden = !state.devToolsEnabled; }
    if (devLogPanel) { devLogPanel.hidden = !state.devToolsEnabled; }
    if (backPicker) { backPicker.hidden = false; }

    renderSummary(state, current);
    renderStepNav(state, steps);
    renderContent(state, current);
    renderMessages(state);
  }

  function renderSummary(state, current) {
    Ui.setText('bridge-status', '主機連線：' + (state.bridgeReady ? '已連線' : '未連線'));
    Ui.setText('case-id', state.caseId || '尚未建立');
    Ui.setText('case-client', state.caseClient || '—');
    Ui.setText('case-step', state.view === 'picker' ? '選擇專案' : current.label);
    Ui.setText('case-progress',
      state.view === 'picker' ? '—' : (state.currentStepIndex + 1) + ' / ' + Store.STEPS.length);
  }

  /* ---- 專案選擇畫面 ----------------------------------------------------- */

  // Phosphor 風 inline SVG（bold/fill，非 thin-line 圖庫、非 emoji）。
  var ICON_TRASH =
    '<svg viewBox="0 0 256 256" width="16" height="16" fill="currentColor" aria-hidden="true">' +
    '<path d="M216 48h-40v-8a24 24 0 0 0-24-24h-48a24 24 0 0 0-24 24v8H40a8 8 0 0 0 0 16h8v144a16 16 0 0 0 16 16h128a16 16 0 0 0 16-16V64h8a8 8 0 0 0 0-16ZM96 40a8 8 0 0 1 8-8h48a8 8 0 0 1 8 8v8H96Zm96 168H64V64h128Zm-80-104v64a8 8 0 0 1-16 0v-64a8 8 0 0 1 16 0Zm48 0v64a8 8 0 0 1-16 0v-64a8 8 0 0 1 16 0Z"></path></svg>';
  var ICON_PLUS =
    '<svg viewBox="0 0 256 256" width="16" height="16" fill="currentColor" aria-hidden="true">' +
    '<path d="M224 128a8 8 0 0 1-8 8h-80v80a8 8 0 0 1-16 0v-80H40a8 8 0 0 1 0-16h80V40a8 8 0 0 1 16 0v80h80a8 8 0 0 1 8 8Z"></path></svg>';

  function providerText(provider) {
    return provider === 'sqlServer' ? 'SQL Server' : 'SQLite';
  }

  function providerClass(provider) {
    return provider === 'sqlServer' ? 'project-provider--sqlserver' : 'project-provider--sqlite';
  }

  // 上次開啟時間（lastOpenedUtc 有值則顯示；否則 fallback 建立時間）。
  function pickerWhen(p) {
    var iso = p.lastOpenedUtc || p.createdUtc;
    if (!iso) { return ''; }
    var date = new Date(iso);
    if (isNaN(date.getTime())) { return ''; }
    var prefix = p.lastOpenedUtc ? '上次開啟 ' : '建立 ';
    return prefix + date.toLocaleString('zh-Hant', {
      year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false
    });
  }

  function renderPicker(state) {
    var container = Ui.$('project-picker');
    if (!container) { return; }

    var key = 'picker|' + state.contentVersion;
    if (key === lastPickerKey) { return; }
    lastPickerKey = key;

    var rows = state.projects.map(function (p) {
      return (
        '<div class="project-row" role="button" tabindex="0" data-action="picker-open" data-project-id="' +
          Ui.esc(p.projectId) + '">' +
          '<span class="project-row__main">' +
            '<span class="project-row__title">' + Ui.esc(p.entityName) + '</span>' +
            '<span class="project-row__meta">' +
              '<span class="project-row__code">' + Ui.esc(p.projectCode) + '</span>' +
              '<span class="project-provider ' + providerClass(p.databaseProvider) + '">' +
                Ui.esc(providerText(p.databaseProvider)) + '</span>' +
              '<span class="project-row__when">' + Ui.esc(pickerWhen(p)) + '</span>' +
            '</span>' +
          '</span>' +
          '<button type="button" class="project-row__delete" data-action="picker-delete" ' +
            'data-project-id="' + Ui.esc(p.projectId) + '" data-project-name="' + Ui.esc(p.entityName) + '" ' +
            'title="刪除專案" aria-label="刪除專案">' + ICON_TRASH + '</button>' +
        '</div>'
      );
    }).join('');

    container.innerHTML =
      '<div class="picker-panel">' +
        '<h2 class="picker-panel__title">選擇專案</h2>' +
        '<p class="picker-panel__hint">點整列即可開啟既有查核專案，或從最底列新增專案開始。</p>' +
        '<div class="picker-panel__list">' +
          rows +
          '<div class="project-row project-row--new" role="button" tabindex="0" data-action="picker-new">' +
            '<span class="project-row__plus">' + ICON_PLUS + '</span>' +
            '<span class="project-row__main">' +
              '<span class="project-row__title">新增專案</span>' +
            '</span>' +
          '</div>' +
        '</div>' +
        '<div class="picker-panel__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="picker-refresh">重新整理</button>' +
        '</div>' +
      '</div>' +
      '<div class="modal" data-bind="delete-modal" hidden>' +
        '<div class="modal__backdrop" data-action="modal-cancel"></div>' +
        '<div class="modal__card" role="dialog" aria-modal="true" aria-labelledby="delete-modal-title">' +
          '<h3 class="modal__title" id="delete-modal-title">刪除專案</h3>' +
          '<p class="modal__body">確定要刪除「<span data-bind="delete-modal-name"></span>」嗎？' +
            '此操作會一併刪除該專案的資料庫，<strong>無法復原</strong>。</p>' +
          '<div class="modal__actions">' +
            '<button type="button" class="btn btn--ghost" data-action="modal-cancel">取消</button>' +
            '<button type="button" class="btn btn--danger" data-action="modal-confirm">刪除</button>' +
          '</div>' +
        '</div>' +
      '</div>';

    bindPicker(container);
  }

  // 待確認刪除的目標（modal 開啟期間）；確認後清空。
  var pendingDelete = null;

  function bindPicker(container) {
    var modal = container.querySelector('[data-bind="delete-modal"]');

    function openCreate() {
      // 清空殘留的舊案件狀態，確保建立案件步驟是全新表單。
      Store.resetWorkflow();
      Store.setView('workflow');
    }

    function closeModal() {
      pendingDelete = null;
      if (modal) { modal.hidden = true; }
    }

    // 整列（含新增列）可點 + 鍵盤 Enter/Space 觸發。
    container.querySelectorAll('[data-action="picker-open"], [data-action="picker-new"]').forEach(function (row) {
      var isNew = row.getAttribute('data-action') === 'picker-new';
      var activate = isNew
        ? openCreate
        : function () { Ui.openProject(row.getAttribute('data-project-id')); };

      row.addEventListener('click', activate);
      row.addEventListener('keydown', function (event) {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          activate();
        }
      });
    });

    // 刪除 icon：不冒泡觸發整列開啟，改開確認框。
    container.querySelectorAll('[data-action="picker-delete"]').forEach(function (btn) {
      btn.addEventListener('click', function (event) {
        event.stopPropagation();
        pendingDelete = {
          id: btn.getAttribute('data-project-id'),
          name: btn.getAttribute('data-project-name')
        };
        if (modal) {
          modal.querySelector('[data-bind="delete-modal-name"]').textContent = pendingDelete.name;
          modal.hidden = false;
        }
      });
    });

    container.querySelector('[data-action="picker-refresh"]').addEventListener('click', Ui.loadProjects);

    if (modal) {
      modal.querySelectorAll('[data-action="modal-cancel"]').forEach(function (el) {
        el.addEventListener('click', closeModal);
      });

      modal.querySelector('[data-action="modal-confirm"]').addEventListener('click', function () {
        if (!pendingDelete) { return; }
        var target = pendingDelete;
        closeModal();
        Ui.run('刪除專案', function () {
          return global.JetApi.projectDelete({ projectId: target.id }).then(function () {
            Store.addMessage('已刪除專案「' + target.name + '」。', 'info');
            return Ui.loadProjects();
          });
        });
      });
    }
  }

  /* ---- 流程步驟導航 ------------------------------------------------------ */

  function renderStepNav(state, steps) {
    var nav = Ui.$('step-nav');
    if (!nav) { return; }

    if (!stepButtons) {
      stepButtons = [];
      nav.innerHTML = '';
      steps.forEach(function (step, index) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'step-nav__item';
        btn.innerHTML =
          '<span class="step-nav__index">' + (index + 1) + '</span>' +
          '<span class="step-nav__label">' + step.label + '</span>';
        btn.addEventListener('click', function () {
          Ui.gotoStep(index);
        });
        nav.appendChild(btn);
        stepButtons.push({ btn: btn, indexEl: btn.querySelector('.step-nav__index') });
      });
    }

    stepButtons.forEach(function (entry, index) {
      var active = index === state.currentStepIndex;
      var reachable = Ui.isStepReachable(state, index);
      // 「完成」＝下一步的進入條件已滿足，即本步驟的必要產出已存在。
      var done = index < steps.length - 1 && Ui.stepGate(state, index + 1).ok;

      entry.btn.classList.toggle('is-active', active);
      entry.btn.classList.toggle('is-done', done);
      entry.btn.disabled = !reachable;
      entry.btn.title = reachable ? '' : Ui.lockedStepTip(state, index);
      entry.indexEl.textContent = done ? '✓' : String(index + 1);

      if (active) {
        entry.btn.setAttribute('aria-current', 'step');
      } else {
        entry.btn.removeAttribute('aria-current');
      }
    });
  }

  /* ---- 中央內容區 -------------------------------------------------------- */

  function renderContent(state, current) {
    var container = Ui.$('content');
    if (!container) { return; }

    // (view, step, contentVersion) 三元組：資料更新會重建面板，
    // 純訊息或 busy 變動不會。
    var key = state.view + '|' + current.id + '|' + state.contentVersion;
    if (key === lastContentKey) { return; }
    lastContentKey = key;

    Ui.renderStep(current.id, container, state);
  }

  /* ---- 訊息區 -------------------------------------------------------------- */

  function renderMessages(state) {
    renderMessagesPanel(state);

    var list = Ui.$('message-list');
    if (!list) { return; }

    if (state.messages.length === 0) {
      if (!messagesEmptyShown) {
        list.innerHTML = '';
        var empty = document.createElement('li');
        empty.className = 'messages__empty';
        empty.textContent = '目前沒有訊息。';
        list.appendChild(empty);
        messagesEmptyShown = true;
      }
      return;
    }

    if (messagesEmptyShown) {
      list.innerHTML = '';
      messagesEmptyShown = false;
    }

    var fresh = [];
    for (var i = 0; i < state.messages.length; i++) {
      if (state.messages[i].id > lastMessageId) {
        fresh.push(state.messages[i]);
      } else {
        break;
      }
    }

    for (var j = fresh.length - 1; j >= 0; j--) {
      list.insertBefore(buildMessage(fresh[j]), list.firstChild);
    }

    if (fresh.length > 0) {
      lastMessageId = state.messages[0].id;
    }

    while (list.children.length > state.messages.length) {
      list.removeChild(list.lastChild);
    }
  }

  function buildMessage(msg) {
    var li = document.createElement('li');
    li.className = 'messages__item messages__item--' + msg.level;
    li.innerHTML =
      '<span class="messages__time">' + msg.time + '</span>' +
      '<span class="messages__text">' + Ui.esc(msg.text) + '</span>';
    return li;
  }

  // 收合/展開與未讀徽章（收合期間有 warn 訊息時徽章轉警示色）
  function renderMessagesPanel(state) {
    var panel = document.querySelector('[data-bind="messages-panel"]');
    if (!panel) { return; }

    panel.classList.toggle('messages--collapsed', !state.messagesPanelOpen);

    var rail = panel.querySelector('.messages__rail');
    if (rail) {
      rail.setAttribute('aria-expanded', state.messagesPanelOpen ? 'true' : 'false');
    }

    var badge = Ui.$('message-badge');
    if (badge) {
      badge.hidden = state.messagesUnseen === 0;
      badge.textContent = state.messagesUnseen > 99 ? '99+' : String(state.messagesUnseen);

      var hasUnseenWarn = false;
      for (var i = 0; i < Math.min(state.messagesUnseen, state.messages.length); i++) {
        if (state.messages[i].level === 'warn') { hasUnseenWarn = true; break; }
      }
      badge.classList.toggle('messages__badge--warn', hasUnseenWarn);
    }
  }

  /* ---- 訊息持久化（log.append；manifest 細節） ------------------------------- */

  // 以 id 水位增量持久化：無 active project 的訊息（專案選擇畫面）只推進水位不落庫;
  // fromLog（log.recent 還原的歷史）不回寫，避免重複。fire-and-forget——持久化失敗不打斷 UX。
  var lastPersistedId = 0;

  function persistNewMessages(state) {
    if (state.messages.length === 0) { return; }

    var canPersist = state.project && state.bridgeReady
      && global.JetApi && typeof global.JetApi.logAppend === 'function';

    for (var i = state.messages.length - 1; i >= 0; i--) {
      var msg = state.messages[i];
      if (msg.id <= lastPersistedId) { continue; }
      lastPersistedId = msg.id;

      if (canPersist && !msg.fromLog) {
        global.JetApi.logAppend({ level: msg.level, text: msg.text }).catch(function () {});
      }
    }
  }

  /* ---- 啟動 ---------------------------------------------------------------- */

  function init() {
    Store.subscribe(render);
    Store.subscribe(persistNewMessages);

    var ready = !!(global.JetApi && global.JetApi.isReady && global.JetApi.isReady());
    Store.setBridgeReady(ready);

    var exitBtn = document.querySelector('[data-action="app-exit"]');
    if (exitBtn) {
      exitBtn.addEventListener('click', Ui.exitApp);
    }

    var backPickerBtn = document.querySelector('[data-action="app-back-picker"]');
    if (backPickerBtn) {
      backPickerBtn.addEventListener('click', Ui.backToPickerFromHeader);
    }

    document.querySelectorAll('[data-action="messages-toggle"]').forEach(function (btn) {
      btn.addEventListener('click', Store.toggleMessagesPanel);
    });

    if (Ui.initDataPreview) { Ui.initDataPreview(); }
    if (Ui.initDevPanel) { Ui.initDevPanel(); }
    if (Ui.initDevLogPanel) { Ui.initDevLogPanel(); }
    Store.addMessage('前端已載入。', 'info');

    if (!ready) {
      Store.addMessage('WebView2 host 尚未連線；以靜態前端模式顯示。', 'warn');
      render();
      return;
    }

    global.JetApi.systemPing({})
      .then(function (ping) {
        Store.setBridgeReady(true);
        Store.setDevToolsEnabled(ping && ping.devToolsEnabled === true);
        return Ui.loadProjects();
      })
      .catch(function (error) {
        Store.setBridgeReady(false);
        Store.addMessage('Bridge 連線失敗：' + error.message, 'warn');
      })
      .finally(render);

    render();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})(window);
