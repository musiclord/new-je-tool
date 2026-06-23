/*
  WebView2 bridge boundary.
  This file is reserved for the single JetApi action channel.
*/
(function (global) {
  'use strict';

  var pending = Object.create(null);

  function isReady() {
    return !!(global.chrome && global.chrome.webview && global.chrome.webview.postMessage);
  }

  function invoke(action, payload) {
    if (!isReady()) {
      return Promise.reject(new Error('JET host bridge is not available.'));
    }

    var requestId = createRequestId();
    var request = {
      requestId: requestId,
      action: action,
      payload: payload || {}
    };

    return new Promise(function (resolve, reject) {
      pending[requestId] = {
        resolve: resolve,
        reject: reject
      };

      global.chrome.webview.postMessage(request);
    });
  }

  function createRequestId() {
    if (global.crypto && typeof global.crypto.randomUUID === 'function') {
      return global.crypto.randomUUID();
    }

    return 'jet-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
  }

  // host→web 事件訂閱表（manifest「Host→Web 事件」：信封 { event, data }、無 requestId）
  var eventHandlers = Object.create(null);

  function receive(event) {
    var message = event.data;
    if (typeof message === 'string') {
      try {
        message = JSON.parse(message);
      } catch (error) {
        return;
      }
    }

    if (!message) {
      return;
    }

    if (message.event) {
      var handlers = eventHandlers[message.event];
      if (handlers) {
        handlers.slice().forEach(function (handler) {
          try {
            handler(message.data);
          } catch (error) {
            // 單一訂閱者出錯不得打斷其他訂閱者；事件是 UX 提示，不承載權威
            if (global.console) { global.console.error('JET event handler failed:', error); }
          }
        });
      }
      return;
    }

    if (!message.requestId || !pending[message.requestId]) {
      return;
    }

    var callbacks = pending[message.requestId];
    delete pending[message.requestId];

    if (message.ok) {
      callbacks.resolve(message.data);
      return;
    }

    var detail = message.error && message.error.message
      ? message.error.message
      : 'JET bridge request failed.';

    callbacks.reject(new Error(detail));
  }

  if (isReady() && typeof global.chrome.webview.addEventListener === 'function') {
    global.chrome.webview.addEventListener('message', receive);
  }

  // 正式契約 actions（docs/action-contract-manifest.md 為 source of truth）。
  // 新增 action：先改 manifest，再加進這份清單，最後才能在 UI 呼叫 JetApi.<method>。
  var SUPPORTED_ACTIONS = [
    'system.ping',
    'project.list',
    'project.create',
    'project.load',
    'project.delete',
    'project.saveProgress',
    'project.loadDemo',
    'demo.exportGlFile',
    'demo.exportTbFile',
    'demo.exportAccountMappingFile',
    'demo.exportAuthorizedPreparerFile',
    'import.gl.fromFile',
    'import.tb.fromFile',
    'import.accountMapping.fromFile',
    'import.authorizedPreparer.fromFile',
    'import.inspectFile',
    'import.previewFile',
    'import.holiday',
    'import.makeupDay',
    'import.holiday.fromFile',
    'import.makeupDay.fromFile',
    'mapping.autoSuggest',
    'mapping.commit.gl',
    'mapping.commit.tb',
    'validate.run',
    'prescreen.run',
    'filter.preview',
    'filter.commit',
    'query.dataPreview',
    'query.completenessDiffPage',
    'query.docBalancePage',
    'query.nullRecordsPage',
    'query.filterHitsPage',
    'query.infSamplePage',
    'query.tagMatrixScenarios',
    'query.tagMatrixVoucherPage',
    'query.tagMatrixRowPage',
    'export.workpaperStream',
    'log.append',
    'log.recent',
    'host.selectFile',
    'host.selectFiles',
    'host.selectSavePath',
    'host.openFolder',
    'host.exitApp',
    'dev.db.overview',
    'dev.db.tableData',
    'dev.log.export'
  ];

  // action name → lowerCamelCase method：第一段保留小寫，後續段首字母大寫。
  // 例：mapping.commit.gl → mappingCommitGl
  function toMethodName(action) {
    var parts = action.split('.');
    var name = parts[0];
    for (var i = 1; i < parts.length; i++) {
      name += parts[i].charAt(0).toUpperCase() + parts[i].slice(1);
    }
    return name;
  }

  // host→web 事件訂閱（manifest「Host→Web 事件」）。handler 收到 data 物件。
  function on(eventName, handler) {
    (eventHandlers[eventName] = eventHandlers[eventName] || []).push(handler);
  }

  function off(eventName, handler) {
    var handlers = eventHandlers[eventName];
    if (!handlers) { return; }
    var index = handlers.indexOf(handler);
    if (index >= 0) { handlers.splice(index, 1); }
  }

  var JetApi = {
    isReady: isReady,
    invoke: invoke,
    on: on,
    off: off
  };

  SUPPORTED_ACTIONS.forEach(function (action) {
    JetApi[toMethodName(action)] = function (payload) {
      return invoke(action, payload);
    };
  });

  global.JetApi = JetApi;
})(window);
