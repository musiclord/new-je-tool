/*
  資料預覽（正式版功能；manifest query.dataPreview）。
  讓使用者直觀看到目前操作的資料長什麼樣子：
  - 欄位配對時對照「欄名 ↔ 實際內容」（來源原貌資料集）
  - 進階篩選前掌握數值/日期/摘要的大概樣貌（標準化後資料集 + 概況統計）
  有界預覽（≤50 列 + 總列數），絕不載入完整母體；權威計算一律在後端。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  var DATASETS = [
    { value: 'glStaging', label: 'GL 來源資料（匯入原貌）' },
    { value: 'tbStaging', label: 'TB 來源資料（匯入原貌）' },
    { value: 'glEntries', label: 'GL 標準化後分錄（測試母體）' },
    { value: 'tbBalances', label: 'TB 標準化後餘額' },
    { value: 'accountMappings', label: '科目配對（標準化分類）' },
    { value: 'authorizedPreparers', label: '授權編製人員清單' }
  ];

  // 標準化後資料集的固定欄位 id → 顯示標籤（manifest query.dataPreview 細節段）
  var COLUMN_LABELS = {
    documentNumber: '傳票號碼',
    lineItem: '項次',
    postDate: '總帳日期',
    accountCode: '科目編號',
    accountName: '科目名稱',
    documentDescription: '摘要',
    amount: '金額',
    drCr: '借貸',
    changeAmount: '變動金額',
    standardizedCategory: '標準化分類',
    preparerName: '姓名'
  };

  var loadedOnce = false;

  function initDataPreview() {
    var body = Ui.$('data-preview-body');
    if (!body) { return; }

    var options = DATASETS.map(function (d) {
      return '<option value="' + d.value + '">' + d.label + '</option>';
    }).join('');

    body.innerHTML =
      '<div class="data-preview__controls">' +
        '<select class="data-preview__select" data-bind="data-preview-dataset">' + options + '</select>' +
        '<button type="button" class="btn btn--ghost" data-action="data-preview-refresh">重新整理</button>' +
      '</div>' +
      '<p class="data-preview__info" data-bind="data-preview-info">開啟後按「重新整理」載入目前資料。</p>' +
      '<div class="data-preview__grid" data-bind="data-preview-grid"></div>';

    body.querySelector('[data-action="data-preview-refresh"]').addEventListener('click', refresh);
    body.querySelector('[data-bind="data-preview-dataset"]').addEventListener('change', refresh);

    // 展開面板時自動載入一次（之後以「重新整理」取得最新狀態）
    var details = Ui.$('data-preview');
    if (details) {
      details.addEventListener('toggle', function () {
        if (details.open && !loadedOnce) { refresh(); }
      });
    }
  }

  // 步驟內的「預覽資料」按鈕入口：展開面板、切到對應資料集並載入。
  function openDataPreview(dataset) {
    var details = Ui.$('data-preview');
    var select = Ui.$('data-preview-dataset');
    if (!details || !select) { return; }

    select.value = dataset;
    details.open = true;
    refresh();
    details.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  function refresh() {
    var select = Ui.$('data-preview-dataset');
    if (!select || !Store.getState().project) {
      Store.addMessage('請先建立或載入專案，再預覽資料。', 'warn');
      return;
    }

    var dataset = select.value;
    loadedOnce = true;

    Ui.run('載入資料預覽', function () {
      return global.JetApi.queryDataPreview({ dataset: dataset }).then(function (data) {
        renderPreview(dataset, data);
      });
    });
  }

  function renderPreview(dataset, data) {
    var info = Ui.$('data-preview-info');
    var grid = Ui.$('data-preview-grid');
    if (!info || !grid) { return; }

    if (data.totalCount === 0) {
      info.textContent = emptyHint(dataset);
      grid.innerHTML = '';
      return;
    }

    var parts = ['共 ' + Number(data.totalCount).toLocaleString() + ' 列，顯示前 ' + data.rows.length + ' 列'];

    // glEntries 概況統計：進階篩選設定數值/日期區間前的把關資訊
    if (data.stats) {
      parts.push('金額（絕對值）' + Number(data.stats.amountAbsMin).toLocaleString() +
        ' ～ ' + Number(data.stats.amountAbsMax).toLocaleString());
      if (data.stats.postDateMin) {
        parts.push('總帳日期 ' + data.stats.postDateMin + ' ～ ' + data.stats.postDateMax);
      }
      parts.push('傳票 ' + Number(data.stats.voucherCount).toLocaleString() + ' 張');
    }

    info.textContent = parts.join(' ｜ ');

    var head = '<tr>' + data.columns.map(function (c) {
      return '<th>' + Ui.esc(COLUMN_LABELS[c] || c) + '</th>';
    }).join('') + '</tr>';

    var body = data.rows.map(function (row) {
      return '<tr>' + row.map(function (cell) {
        return cell === null
          ? '<td class="is-null">—</td>'
          : '<td>' + Ui.esc(cell) + '</td>';
      }).join('') + '</tr>';
    }).join('');

    grid.innerHTML =
      '<table class="data-preview__table"><thead>' + head + '</thead><tbody>' + body + '</tbody></table>';
  }

  function emptyHint(dataset) {
    switch (dataset) {
      case 'glStaging': return '尚未匯入 GL 資料；完成「匯入資料」後即可預覽來源原貌。';
      case 'tbStaging': return '尚未匯入 TB 資料；完成「匯入資料」後即可預覽來源原貌。';
      case 'glEntries': return '尚未產生標準化分錄；完成「欄位配對」的確認後即可預覽測試母體。';
      case 'tbBalances': return '尚未產生標準化餘額；完成「欄位配對」的確認後即可預覽。';
      case 'accountMappings': return '尚未匯入科目配對；在「匯入資料」步驟選擇科目配對檔後即可預覽。';
      case 'authorizedPreparers': return '尚未匯入授權編製人員清單；在「匯入資料」步驟匯入後即可預覽。';
      default: return '目前沒有資料。';
    }
  }

  Ui.initDataPreview = initDataPreview;
  Ui.openDataPreview = openDataPreview;
})(window);
