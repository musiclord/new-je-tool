/*
  Step 5：匯出底稿。
  選存檔路徑（host.selectSavePath）→ 後端串流寫出 .xlsx（export.workpaperStream）→ 顯示完成統計。
  前端零商業邏輯：哪些工作表、各表內容、條件表是否出現、金額換算全部在後端 writer 決定；
  本步驟只組預填檔名（公司名來自既有專案狀態）、發 action、呈現後端回的統計（bytesWritten + 各表列數）。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  // 最近一次匯出的完成統計（區域 UI 狀態；離開專案時清除）。
  // { path, bytesWritten, sheetStats:[{sheetName, rowsWritten}] } | null
  var lastResult = null;

  Ui.registerWorkflowReset(function () { lastResult = null; });

  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('匯出底稿');
      Ui.bindNoProjectPanel(container);
      return;
    }

    var scenarioCount = (state.filter.savedScenarios || []).length;

    container.innerHTML =
      '<div class="panel">' +
        '<h2 class="panel__title">匯出底稿</h2>' +
        '<p class="panel__hint">將查核底稿（封面、完整性、可靠性、高風險矩陣與參考資料表）' +
          '串流寫出為 Excel 活頁簿（.xlsx）。已保存的高風險情境會在匯出時納入 step3／step4 矩陣。</p>' +
        '<p class="panel__hint">目前已保存 ' + scenarioCount + ' 個篩選情境。</p>' +
        '<div class="panel__actions">' +
          '<button type="button" class="btn" data-action="export-workpaper">匯出底稿至專案目錄</button>' +
        '</div>' +
        resultHtml() +
        Ui.stepFooterHtml(state) +
      '</div>';

    bindExport(container);
    Ui.bindStepFooter(container);
  }

  // 完成統計區塊（後端回的形狀直接呈現；前端不重算）。
  function resultHtml() {
    if (!lastResult) { return ''; }

    var rows = (lastResult.sheetStats || []).map(function (s) {
      return '<tr><td>' + Ui.esc(s.sheetName) + '</td>' +
        '<td class="preview-table__amount">' + Number(s.rowsWritten).toLocaleString() + '</td></tr>';
    }).join('');

    return (
      '<div class="panel__slot">' +
        '<p>匯出完成：' + Ui.esc(lastResult.path) + '</p>' +
        '<p>檔案大小：' + Number(lastResult.bytesWritten).toLocaleString() + ' 位元組</p>' +
        '<div class="panel__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="open-folder">打開目錄</button>' +
        '</div>' +
        '<table class="preview-table">' +
          '<thead><tr><th>工作表</th><th>資料列數</th></tr></thead>' +
          '<tbody>' + rows + '</tbody>' +
        '</table>' +
      '</div>'
    );
  }

  function bindExport(container) {
    var btn = container.querySelector('[data-action="export-workpaper"]');
    if (btn) {
      btn.addEventListener('click', function () {
        Ui.run('匯出底稿', function () {
          // 直接匯出至專案目錄：不選路徑（後端依專案目錄 + 公司名 + 時間戳落檔，回實際路徑）。
          // sheets 省略 = 全部工作表（哪些表存在由後端條件決定，前端不枚舉以免商業知識外洩）。
          return global.JetApi.exportWorkpaperStream({}).then(function (data) {
            lastResult = {
              path: data.outputPath,
              bytesWritten: data.bytesWritten,
              sheetStats: data.sheetStats || []
            };
            Store.addMessage('已匯出底稿至專案目錄（' + (data.sheetStats || []).length + ' 張工作表）。', 'info');
            Store.touch();
          });
        });
      });
    }

    // 「打開目錄」：在檔案總管揭示剛匯出的底稿（開資料夾並選取該檔）。
    var openBtn = container.querySelector('[data-action="open-folder"]');
    if (openBtn) {
      openBtn.addEventListener('click', function () {
        if (!lastResult || !lastResult.path) { return; }
        Ui.run('打開目錄', function () {
          return global.JetApi.hostOpenFolder({ path: lastResult.path });
        });
      });
    }
  }

  Ui.registerStep('export', render);
})(window);
