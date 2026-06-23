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

  // wire key → 顯示標籤。標籤鏡射 Domain JetSchemaCatalog 的正準審計名（catalog 是命名的
  // 單一事實來源；前端只鏡射其正準名 + 一句中文描述，屬顯示層，不在此自創名稱）。
  var DATASETS = [
    { value: 'glStaging', label: 'JE_PBC（GL 匯入原貌）' },
    { value: 'tbStaging', label: 'TB_PBC（TB 匯入原貌）' },
    { value: 'glEntries', label: 'JE（標準化分錄·測試母體）' },
    { value: 'tbBalances', label: 'TB（標準化試算表餘額）' },
    { value: 'accountMappings', label: 'ACCOUNT_MAPPING（科目對照）' },
    { value: 'authorizedPreparers', label: 'AUTHORIZED_PREPARER（授權編製人員）' },
    { value: 'dateDimension', label: 'DATE_DIMENSION（假日／補班日）' },
    { value: 'schemaOverview', label: '資料庫結構總覽' }
  ];

  // 固定欄位 id → 顯示標籤（manifest query.dataPreview 細節段）
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
    preparerName: '姓名',
    // dateDimension
    date: '日期',
    dayType: '類別',
    dayName: '說明',
    // schemaOverview（結構總覽，rows 來自 catalog metadata）
    canonicalName: '正規名',
    physicalName: '實體名',
    layer: '層',
    audience: '用途',
    browsable: '可瀏覽'
  };

  // dateDimension 的 dayType 原值（holiday/makeup）→ 中文顯示
  var DAY_TYPE_LABELS = { holiday: '假日', makeup: '補班' };

  // 純顯示層的 cell 改寫（不改後端語意）：dayType 原值轉中文，其餘照原樣。
  function displayCell(dataset, column, cell) {
    if (cell === null) { return null; }
    if (dataset === 'dateDimension' && column === 'dayType') {
      return DAY_TYPE_LABELS[cell] || cell;
    }
    return cell;
  }

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
      return '<tr>' + row.map(function (cell, i) {
        var shown = displayCell(dataset, data.columns[i], cell);
        return shown === null
          ? '<td class="is-null">—</td>'
          : '<td>' + Ui.esc(shown) + '</td>';
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
      case 'dateDimension': return '尚未匯入事務所假日／補班日；在「匯入資料」步驟匯入行事曆後即可預覽。';
      default: return '目前沒有資料。';
    }
  }

  Ui.initDataPreview = initDataPreview;
  Ui.openDataPreview = openDataPreview;
})(window);
