/*
  Step 3：資料驗證與測試（四項資料驗證 + 預篩選合併步驟）。
  規則名稱對齊 guide §4 命名登錄表（V/R 代號已退役）；
  標題與說明只是 presentation，權威規則一律在後端 SQL。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  function naStatus(rule) {
    return { tone: 'na', text: '無法執行', tip: rule.naReason };
  }

  // 命中型規則（row-tag）：命中是「值得留意」而非「錯誤」。
  // rule 為 null/undefined 表示這次 run 不含此規則（resume 舊版結果缺新增鍵）→ 視為「未執行」，
  // 不可讓缺鍵的舊結果在 render 階段丟例外而拖垮整個步驟。
  function hitCountStatus(rule, tip) {
    if (!rule) { return null; }
    if (rule.naReason) { return naStatus(rule); }
    var n = Number(rule.count);
    return n > 0
      ? { tone: 'hit', text: n.toLocaleString() + ' 筆', tip: tip }
      : { tone: 'ok', text: '未發現', tip: tip };
  }

  // 過帳／核准成對計數（週末／假日規則）。approvalCount 為 null 表示核准日未配對。
  // rule 缺漏（resume 舊版結果）→「未執行」，理由同 hitCountStatus。
  function pairCountStatus(rule) {
    if (!rule) { return null; }
    if (rule.naReason) { return naStatus(rule); }
    var approval = rule.approvalCount == null ? '—' : Number(rule.approvalCount).toLocaleString();
    var any = Number(rule.postingCount) > 0 || Number(rule.approvalCount) > 0;
    return {
      tone: any ? 'hit' : 'ok',
      text: '過帳 ' + Number(rule.postingCount).toLocaleString() + '・核准 ' + approval,
      tip: rule.approvalCount == null ? '未配對「傳票核准日」欄位，核准面無法計算。' : undefined
    };
  }

  // 預篩選命中預覽快取：以 runId + '|' + prescreenKey 為鍵，存已載入的 HTML 字串。
  // 每次新的 run（新 runId）自然不命中快取，重新抓取。
  var prescreenPreviewCache = {};

  // 建立單條件情境 AST，供 filter.preview 評估特定預篩選鍵的命中列。
  // name 與 rationale 為後端驗證必填（非空），固定填入有意義的值。
  function prescreenScenario(key) {
    var rule = Ui.newFilterRule('prescreen');
    rule.prescreenKey = key;
    return { name: '命中預覽', rationale: '預篩選命中預覽', groups: [{ join: 'AND', rules: [rule] }] };
  }

  // 詳情渲染輔助：把 detail 物件轉成 HTML 字串（放進 hidden 容器）。
  // prescreenPreview 類型回傳佔位 HTML；展開時觸發惰性抓取（見 bind()）。
  function detailHtml(detail) {
    if (!detail) { return ''; }
    if (detail.kind === 'reason') {
      return '<p class="rule-detail__reason">' + Ui.esc(detail.text) + '</p>' +
             '<p class="rule-detail__remedy">' + Ui.esc(detail.remedy) + '</p>';
    }
    if (detail.kind === 'table') {
      var thead = '<tr>' + detail.columns.map(function (c) {
        return '<th>' + Ui.esc(c) + '</th>';
      }).join('') + '</tr>';
      var tbody = detail.rows.map(function (row) {
        return '<tr>' + row.map(function (cell) {
          return '<td>' + Ui.esc(String(cell == null ? '' : cell)) + '</td>';
        }).join('') + '</tr>';
      }).join('');
      var prefix = detail.prefix ? '<p class="rule-detail__prefix">' + Ui.esc(detail.prefix) + '</p>' : '';
      // loadMore：把後端首屏 ≤50 預覽下方接「載入更多」鈕(keyset)。
      // detail.loadMore 可為單一 id 字串或 id 陣列(空值「傳票號／科目」一格 tbody 兩 category 各一鈕)。
      // tbody 標記 data-load-target(用第一個 id 對齊),每顆鈕標記 data-load-more（bind() 用 JetUi.bindLoadMore 綁定）。
      var lmIds = detail.loadMore == null ? [] :
        (Array.isArray(detail.loadMore) ? detail.loadMore : [detail.loadMore]);
      var targetAttr = lmIds.length ? ' data-load-target="' + Ui.esc(lmIds[0]) + '"' : '';
      var buttons = lmIds.map(function (id) { return loadMoreButtonHtml(id); }).join('');
      return prefix +
        '<div class="preview-table__wrap">' +
          '<table class="preview-table"><thead>' + thead + '</thead>' +
          '<tbody' + targetAttr + '>' + tbody + '</tbody></table>' +
        '</div>' +
        buttons;
    }
    if (detail.kind === 'prescreenPreview') {
      // 每個 preview 項目產生一個帶佔位符的子區塊；data-prescreen-key 供 bind() 識別。
      return detail.previews.map(function (pv) {
        return '<div class="rule-detail__preview-block" data-prescreen-key="' + Ui.esc(pv.key) + '">' +
          '<span class="rule-detail__preview-label">' + Ui.esc(pv.label) + '</span>' +
          '<div class="rule-detail__preview-body">載入中…</div>' +
        '</div>';
      }).join('');
    }
    return '';
  }

  // 「載入更多」鈕標記:沿用既有 btn--ghost btn--tiny;data-load-more 對應同 id 的 tbody。
  function loadMoreButtonHtml(id) {
    return '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more"' +
      ' data-load-more="' + Ui.esc(id) + '">載入更多</button>';
  }

  function num(v) { return Number(v).toLocaleString(); }

  // 「載入更多」規格表:id → { fetchPage(cursor)、columns(接列欄序與既有 table 對齊) }。
  // fetchPage 只發對應 query.*Page action、回 { rows, nextCursor };columns 為每欄的 row→cell 取值函式。
  // 純呼叫膠水:不算差異、不判命中、不組 SQL;顯示形狀(金額換算、借貸文字)由後端 row 決定,前端僅格式化呈現。
  var LOAD_MORE_SPECS = {
    completeness: {
      fetchPage: function (cursor) {
        return global.JetApi.queryCompletenessDiffPage({ cursor: cursor, pageSize: 200 });
      },
      columns: [
        function (r) { return r.accountCode || ''; },
        function (r) { return r.accountName || ''; },
        function (r) { return num(r.tbAmount); },
        function (r) { return num(r.glAmount); },
        function (r) { return num(r.diff); }
      ]
    },
    docBalance: {
      fetchPage: function (cursor) {
        return global.JetApi.queryDocBalancePage({ cursor: cursor, pageSize: 200 });
      },
      columns: [
        function (r) { return r.documentNumber || ''; },
        function (r) { return num(r.debit); },
        function (r) { return num(r.credit); },
        function (r) { return num(r.diff); }
      ]
    },
    inf: {
      fetchPage: function (cursor) {
        return global.JetApi.queryInfSamplePage({ cursor: cursor, pageSize: 200 });
      },
      columns: [
        function (r) { return r.documentNumber || ''; },
        function (r) { return (r.accountCode || '') + ' ' + (r.accountName || ''); },
        function (r) { return num(r.debit); },
        function (r) { return num(r.credit); },
        function (r) { return r.postDate || ''; },
        function (r) { return r.approvalDate || ''; },
        function (r) { return r.createdBy || ''; },
        function (r) { return r.approvedBy || ''; },
        function (r) { return r.description || ''; }
      ]
    }
  };

  // 空值三子項各對應一個後端 category 與固定的「異常類別」顯示字;
  // id 形如 null-<category>,動態註冊進 LOAD_MORE_SPECS（columns 與既有空值 table 欄序對齊）。
  function nullLoadMoreSpec(category, categoryLabel) {
    return {
      fetchPage: function (cursor) {
        return global.JetApi.queryNullRecordsPage({ category: category, cursor: cursor, pageSize: 200 });
      },
      columns: [
        function (r) { return r.documentNumber || ''; },
        function (r) { return r.accountCode || ''; },
        function (r) { return r.postDate || ''; },
        function (r) { return r.description || ''; },
        function () { return categoryLabel; }
      ]
    };
  }

  // 後端 nullRecordsPage 白名單四 category → 「異常類別」顯示字(對齊 issuesCn map)。
  LOAD_MORE_SPECS['null-outOfRangeDate'] = nullLoadMoreSpec('outOfRangeDate', '核准日不在期間');
  LOAD_MORE_SPECS['null-nullDescription'] = nullLoadMoreSpec('nullDescription', '空摘要');
  LOAD_MORE_SPECS['null-nullDocument'] = nullLoadMoreSpec('nullDocument', '空傳票號');
  LOAD_MORE_SPECS['null-nullAccount'] = nullLoadMoreSpec('nullAccount', '空科目');

  // issues 陣列（英文鍵）→ 中文顯示，以「、」接合。
  function issuesCn(issues) {
    var map = { account: '空科目', document: '空傳票號', description: '空摘要', date: '核准日不在期間' };
    return (issues || []).map(function (k) { return map[k] || k; }).join('、');
  }

  // 空值紀錄測試在後端是單一規則（null_records_test）；UI 依異常類別拆成三個子項呈現。
  // 每個子項讀對應子計數作徽章，詳情用 nullRows 的 issues 標籤過濾出該類列
  // （明細仍是後端那份 ≤50 有界樣本，故過濾後筆數可能少於徽章數）。
  // categories：後端 nullRecordsPage 的 category 白名單值(一或多個);各對應一顆「載入更多」鈕,
  // 接續逐頁回取該類全量明細(首屏仍是後端 ≤50 有界樣本)。
  function nullSubItem(title, desc, issueKeys, countOf, categories) {
    return {
      title: title,
      desc: desc,
      status: function (v) {
        if (!v) { return null; }
        var n = countOf(v.nullRecordsTest);
        return n > 0
          ? { tone: 'alert', text: n.toLocaleString() + ' 筆異常' }
          : { tone: 'ok', text: '通過' };
      },
      detail: function (v) {
        if (!v || countOf(v.nullRecordsTest) <= 0) { return null; }
        var rows = (v.nullRecordsTest.nullRows || []).filter(function (r) {
          return (r.issues || []).some(function (k) { return issueKeys.indexOf(k) >= 0; });
        }).map(function (r) {
          return [r.documentNumber || '', r.accountCode || '', r.postDate || '', r.description || '', issuesCn(r.issues)];
        });
        return {
          kind: 'table',
          columns: ['傳票號', '科目', '日期', '摘要', '異常類別'],
          rows: rows,
          loadMore: (categories || []).map(function (c) { return 'null-' + c; })
        };
      }
    };
  }

  var VALIDATION_ITEMS = [
    {
      title: '完整性測試',
      desc: '逐科目比對 GL 加總與 TB 期間變動額；金額不符代表總帳母體可能缺漏。',
      status: function (v) {
        if (!v) { return null; }
        if (v.completenessTest.naReason) { return naStatus(v.completenessTest); }
        var n = Number(v.completenessTest.diffAccountCount);
        return n > 0
          ? { tone: 'alert', text: n.toLocaleString() + ' 個科目不符' }
          : { tone: 'ok', text: '通過' };
      },
      detail: function (v) {
        if (!v) { return null; }
        if (v.completenessTest.naReason) {
          return { kind: 'reason', text: v.completenessTest.naReason, remedy: '先到「欄位配對」提交 TB 配對' };
        }
        if (Number(v.completenessTest.diffAccountCount) > 0) {
          var rows = (v.completenessTest.diffAccounts || []).map(function (d) {
            return [
              d.accountCode || '',
              d.accountName || '',
              Number(d.tbAmount).toLocaleString(),
              Number(d.glAmount).toLocaleString(),
              Number(d.diff).toLocaleString()
            ];
          });
          return {
            kind: 'table',
            columns: ['科目代碼', '科目名稱', 'TB 金額', 'GL 金額', '差額'],
            rows: rows,
            loadMore: 'completeness'
          };
        }
        return null;
      }
    },
    {
      title: '借貸不平測試',
      desc: '逐張傳票檢查借方與貸方合計是否為零；不平衡多為資料品質問題。',
      status: function (v) {
        if (!v) { return null; }
        var n = Number(v.docBalanceTest.unbalancedDocumentCount);
        return n > 0
          ? { tone: 'alert', text: n.toLocaleString() + ' 張傳票不平' }
          : { tone: 'ok', text: '通過' };
      },
      detail: function (v) {
        if (!v) { return null; }
        if (Number(v.docBalanceTest.unbalancedDocumentCount) > 0) {
          var rows = (v.docBalanceTest.unbalancedDocuments || []).map(function (d) {
            return [
              d.documentNumber || '',
              Number(d.debit).toLocaleString(),
              Number(d.credit).toLocaleString(),
              Number(d.diff).toLocaleString()
            ];
          });
          return {
            kind: 'table',
            columns: ['傳票號', '借方', '貸方', '差額'],
            rows: rows,
            loadMore: 'docBalance'
          };
        }
        return null;
      }
    },
    {
      title: 'INF 抽樣測試',
      desc: '以固定種子抽出可重現的樣本，供人工核對摘要、日期等非財務欄位的可靠性。',
      status: function (v) {
        if (!v) { return null; }
        if (v.infSamplingTest.naReason) { return naStatus(v.infSamplingTest); }
        return { tone: 'info', text: '已抽出 ' + Number(v.infSamplingTest.sampleSize).toLocaleString() + ' 筆' };
      },
      detail: function (v) {
        // INF 抽樣明細:可展開空表 + 「載入更多」逐頁回取行層樣本(query.infSamplePage)。
        if (!v || v.infSamplingTest.naReason) { return null; }
        if (Number(v.infSamplingTest.sampleSize) <= 0) { return null; }
        return {
          kind: 'table',
          columns: ['傳票號', '科目', '借方', '貸方', '總帳日期', '核准日', '建立人員', '核准人員', '摘要'],
          rows: [],
          prefix: '抽樣樣本明細逐頁載入,按「載入更多」展開。',
          loadMore: 'inf'
        };
      }
    },
    nullSubItem('核准日不在期間', '核准日（確認日期）落在查核期間之外的分錄。', ['date'],
      function (nrt) { return Number(nrt.outOfRangeDateCount); }, ['outOfRangeDate']),
    nullSubItem('空白摘要', '摘要欄位空白的分錄。', ['description'],
      function (nrt) { return Number(nrt.nullDescriptionCount); }, ['nullDescription']),
    nullSubItem('空白傳票號碼／科目', '傳票號碼或科目空白的分錄。', ['document', 'account'],
      function (nrt) { return Number(nrt.nullDocumentCount) + Number(nrt.nullAccountCount); },
      ['nullDocument', 'nullAccount'])
  ];

  var PRESCREEN_ITEMS = [
    {
      title: '期末財報準備日後核准之分錄',
      desc: '結帳日之後才核准的分錄，可能用於操縱期末數字。',
      status: function (p) { return p ? hitCountStatus(p.postPeriodApproval) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.postPeriodApproval.naReason) {
          return { kind: 'reason', text: p.postPeriodApproval.naReason, remedy: '確認查核期間與核准日欄位配對正確' };
        }
        if (Number(p.postPeriodApproval.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'postPeriodApproval' }] };
        }
        return null;
      }
    },
    {
      title: '分錄摘要出現特定描述',
      desc: '摘要出現「調整、沖銷、錯誤」等預設關鍵字的分錄。',
      status: function (p) { return p ? hitCountStatus(p.suspiciousKeywords) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.suspiciousKeywords.naReason) {
          return { kind: 'reason', text: p.suspiciousKeywords.naReason, remedy: '確認摘要欄位配對正確' };
        }
        if (Number(p.suspiciousKeywords.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'suspiciousKeywords' }] };
        }
        return null;
      }
    },
    {
      title: '未預期出現之特定借貸組合',
      desc: '同張傳票同時出現收入貸方與應收／現金借方，為虛增收入的常見模式（需先匯入科目配對）。',
      status: function (p) { return p ? hitCountStatus(p.unexpectedAccountPair) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.unexpectedAccountPair.naReason) {
          return { kind: 'reason', text: p.unexpectedAccountPair.naReason, remedy: '先到「欄位配對」匯入科目配對' };
        }
        if (Number(p.unexpectedAccountPair.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'unexpectedAccountPair' }] };
        }
        return null;
      }
    },
    {
      title: '分錄金額中有連續零的尾數',
      desc: '尾數連續多個 0 的金額（如 1,000,000），常見於估計數或人為填造。',
      status: function (p) {
        return p ? hitCountStatus(p.trailingZeros,
          '本次門檻：尾數連續 ' + p.trailingZeros.zerosThreshold + ' 個 0。') : null;
      },
      detail: function (p) {
        if (!p) { return null; }
        if (p.trailingZeros.naReason) {
          return { kind: 'reason', text: p.trailingZeros.naReason, remedy: '確認金額欄位配對正確' };
        }
        if (Number(p.trailingZeros.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'trailingZeros' }] };
        }
        return null;
      }
    },
    {
      title: '依分錄編製者彙總',
      desc: '依傳票建立人員統計筆數與金額，檢視分錄是否過度集中於少數人（彙總參考）。',
      status: function (p) {
        if (!p) { return null; }
        if (p.creatorSummary.naReason) { return naStatus(p.creatorSummary); }
        return { tone: 'info', text: p.creatorSummary.creators.length + ' 位人員' };
      },
      detail: function (p) {
        if (!p) { return null; }
        if (p.creatorSummary.naReason) {
          return { kind: 'reason', text: p.creatorSummary.naReason, remedy: '確認建立者欄位配對正確' };
        }
        if (p.creatorSummary.creators && p.creatorSummary.creators.length > 0) {
          var rows = p.creatorSummary.creators.map(function (c) {
            return [
              c.createdBy || '',
              Number(c.entryCount).toLocaleString(),
              Number(c.debitTotal).toLocaleString(),
              Number(c.creditTotal).toLocaleString(),
              Number(c.manualCount).toLocaleString()
            ];
          });
          return { kind: 'table', columns: ['人員', '筆數', '借方', '貸方', '人工筆數'], rows: rows };
        }
        return null;
      }
    },
    {
      title: '較少使用之科目',
      desc: '統計各科目使用次數、由低到高排列；低頻科目可能藏有不當分錄（彙總參考）。',
      status: function (p) {
        if (!p) { return null; }
        return { tone: 'info', text: Number(p.rareAccounts.distinctAccountCount).toLocaleString() + ' 個科目' };
      },
      detail: function (p) {
        if (!p) { return null; }
        if (Number(p.rareAccounts.distinctAccountCount) > 0) {
          var rows = (p.rareAccounts.accounts || []).map(function (a) {
            return [
              a.accountCode || '',
              a.accountName || '',
              Number(a.entryCount).toLocaleString(),
              Number(a.debitTotal).toLocaleString(),
              Number(a.creditTotal).toLocaleString()
            ];
          });
          return { kind: 'table', columns: ['科目代碼', '科目名稱', '筆數', '借方', '貸方'], rows: rows };
        }
        return null;
      }
    },
    {
      title: '週末過帳／核准之分錄',
      desc: '總帳日或核准日落在週末的分錄（補班日已排除）。',
      status: function (p) { return p ? pairCountStatus(p.weekendActivity) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.weekendActivity.naReason) {
          return { kind: 'reason', text: p.weekendActivity.naReason, remedy: '確認總帳日期欄位配對正確' };
        }
        var previews = [];
        if (Number(p.weekendActivity.postingCount) > 0) {
          previews.push({ label: '過帳側', key: 'weekendPosting' });
        }
        if (p.weekendActivity.approvalCount != null && Number(p.weekendActivity.approvalCount) > 0) {
          previews.push({ label: '核准側', key: 'weekendApproval' });
        }
        return previews.length > 0 ? { kind: 'prescreenPreview', previews: previews } : null;
      }
    },
    {
      title: '假日過帳／核准之分錄',
      desc: '總帳日或核准日落在國定假日的分錄（需先匯入假日清單）。',
      status: function (p) { return p ? pairCountStatus(p.holidayActivity) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.holidayActivity.naReason) {
          return { kind: 'reason', text: p.holidayActivity.naReason, remedy: '先匯入假日清單' };
        }
        var previews = [];
        if (Number(p.holidayActivity.postingCount) > 0) {
          previews.push({ label: '過帳側', key: 'holidayPosting' });
        }
        if (p.holidayActivity.approvalCount != null && Number(p.holidayActivity.approvalCount) > 0) {
          previews.push({ label: '核准側', key: 'holidayApproval' });
        }
        return previews.length > 0 ? { kind: 'prescreenPreview', previews: previews } : null;
      }
    },
    {
      title: '摘要空白之分錄',
      desc: '摘要未填寫的分錄；可在下一步直接作為篩選條件。',
      status: function (p) { return p ? hitCountStatus(p.blankDescription) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.blankDescription.naReason) {
          return { kind: 'reason', text: p.blankDescription.naReason, remedy: '確認摘要欄位配對正確' };
        }
        if (Number(p.blankDescription.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'blankDescription' }] };
        }
        return null;
      }
    },
    {
      title: '非授權編製人員之分錄',
      desc: '建立人員不在授權編製人員清單中的分錄，可能為越權或冒用帳號（需先匯入授權編製人員清單）。',
      status: function (p) { return p ? hitCountStatus(p.nonAuthorizedPreparer) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.nonAuthorizedPreparer.naReason) {
          return { kind: 'reason', text: p.nonAuthorizedPreparer.naReason,
            remedy: '先到「匯入資料」上傳授權編製人員清單' };
        }
        if (Number(p.nonAuthorizedPreparer.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'nonAuthorizedPreparer' }] };
        }
        return null;
      }
    },
    {
      title: '低頻編製者之分錄',
      desc: '建立人員全期分錄筆數偏少的分錄；過低的活動量可能藏有不當分錄。',
      status: function (p) { return p ? hitCountStatus(p.lowFrequencyPreparer) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.lowFrequencyPreparer.naReason) {
          return { kind: 'reason', text: p.lowFrequencyPreparer.naReason, remedy: '確認建立者欄位配對正確' };
        }
        if (Number(p.lowFrequencyPreparer.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'lowFrequencyPreparer' }] };
        }
        return null;
      }
    },
    {
      title: '低頻科目之分錄',
      desc: '科目全期分錄筆數偏少的分錄；少用科目中的違規或錯誤較易被忽略。',
      status: function (p) { return p ? hitCountStatus(p.lowFrequencyAccount) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (Number(p.lowFrequencyAccount.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'lowFrequencyAccount' }] };
        }
        return null;
      }
    }
  ];

  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('資料驗證與測試');
      Ui.bindNoProjectPanel(container);
      return;
    }

    var ready = !!state.mapping.gl.committed;
    var v = state.lastRuns.validate;
    var p = state.lastRuns.prescreen;

    container.innerHTML =
      '<div class="panel panel--wide">' +
        '<h2 class="panel__title">資料驗證與測試</h2>' +
        '<p class="panel__hint">先驗證資料品質，再掃描風險指標：「資料驗證」確認這份總帳可不可信，' +
          '「風險預篩選」找出哪些分錄值得進一步檢視。所有規則都由後端在資料庫中計算。</p>' +
        (ready ? '' :
          '<p class="panel__warn">尚未確認 GL 欄位配對，無法執行；請先回「欄位配對」完成確認。</p>') +
        statsBarHtml(v) +
        validationCardHtml(v, ready) +
        prescreenCardHtml(p, ready) +
        '<div class="panel__actions">' + Ui.MOCK_BUTTON_HTML + '</div>' +
        Ui.stepFooterHtml(state) +
      '</div>';

    bind(container, ready);
  }

  function statsBarHtml(v) {
    var stats = v && v.stats ? v.stats : null;
    function card(label, value) {
      return (
        '<div class="stat-card">' +
          '<span class="stat-card__value">' + value + '</span>' +
          '<span class="stat-card__label">' + label + '</span>' +
        '</div>'
      );
    }

    return (
      '<div class="stats-bar">' +
        card('GL 分錄筆數', stats ? Number(stats.glRowCount).toLocaleString() : '—') +
        card('傳票數', stats ? Number(stats.voucherCount).toLocaleString() : '—') +
        card('借貸淨額（應為 0）', stats ? Number(stats.net).toLocaleString() : '—') +
        card('查核期間', stats ? Ui.esc(stats.periodStart) + ' ～ ' + Ui.esc(stats.periodEnd) : '—') +
      '</div>'
    );
  }

  // 規則狀態徽章：未執行 → 灰；通過/未發現 → 綠；風險候選 → 黃；
  // 需要處理 → 紅；彙總參考 → 藍；無法執行 → 虛線（tooltip 給原因）。
  function ruleBadge(status) {
    if (!status) {
      return '<span class="rule-status rule-status--idle">未執行</span>';
    }
    return '<span class="rule-status rule-status--' + status.tone + '"' +
      (status.tip ? ' title="' + Ui.esc(status.tip) + '"' : '') + '>' + Ui.esc(status.text) + '</span>';
  }

  function ruleListHtml(items, runData, scope) {
    return '<ul class="rule-list">' + items.map(function (item, idx) {
      // status 為 null = 未執行（無 runData 或 resume 舊結果缺此規則鍵）→ 不算 detail,
      // 避免 detail 去取缺漏子物件而丟例外拖垮整步;詳情只在規則確有結果時才存在。
      var status = item.status(runData);
      var detail = (status && item.detail) ? item.detail(runData) : null;
      var toggleBtn = detail
        ? '<button type="button" class="btn btn--ghost btn--tiny rule-item__toggle"' +
            ' data-action="toggle-detail" data-scope="' + Ui.esc(scope) + '" data-idx="' + idx + '">' +
            '檢視</button>'
        : '';
      var detailContainer = detail
        ? '<div class="rule-detail" data-bind="rule-detail-' + Ui.esc(scope) + '-' + idx + '" hidden>' +
            detailHtml(detail) +
          '</div>'
        : '';
      return (
        '<li class="rule-item">' +
          '<div class="rule-item__main">' +
            '<span class="rule-item__title">' + Ui.esc(item.title) + '</span>' +
            '<span class="rule-item__desc">' + Ui.esc(item.desc) + '</span>' +
          '</div>' +
          ruleBadge(status) +
          toggleBtn +
          detailContainer +
        '</li>'
      );
    }).join('') + '</ul>';
  }

  function lastRunLineHtml(runData) {
    if (!runData || !runData.resultRef) { return ''; }
    var time = new Date(runData.resultRef.generatedUtc);
    return '<p class="rule-card__meta">上次執行：' +
      time.toLocaleString('zh-Hant', { hour12: false }) + '</p>';
  }

  function validationCardHtml(v, ready) {
    return (
      '<section class="rule-card">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title">資料驗證</h3>' +
          '<button type="button" class="btn" data-action="run-validate"' + (ready ? '' : ' disabled') +
            '>' + (v ? '重新執行驗證' : '執行驗證') + '</button>' +
        '</div>' +
        '<p class="rule-card__sub">確認匯入母體完整、平衡且可信；任何一項不通過，後續測試的基礎都可能不可靠。</p>' +
        lastRunLineHtml(v) +
        ruleListHtml(VALIDATION_ITEMS, v, 'v') +
      '</section>'
    );
  }

  function prescreenCardHtml(p, ready) {
    return (
      '<section class="rule-card">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title">風險預篩選</h3>' +
          '<button type="button" class="btn" data-action="run-prescreen"' + (ready ? '' : ' disabled') +
            '>' + (p ? '重新執行預篩選' : '執行預篩選') + '</button>' +
        '</div>' +
        '<p class="rule-card__sub">以固定風險指標掃描全部分錄並回報筆數。「命中」不代表錯誤，而是值得留意的候選；' +
          '下一步可將這些指標組合成篩選情境。</p>' +
        lastRunLineHtml(p) +
        ruleListHtml(PRESCREEN_ITEMS, p, 'p') +
      '</section>'
    );
  }

  function bind(container, ready) {
    Ui.bindMockButton(container, 'validate');
    Ui.bindStepFooter(container);

    // 「載入更多」綁定:每顆鈕依 id 查 LOAD_MORE_SPECS 取 fetchPage + columns,
    // 接列目標 tbody 以 data-load-target=<第一個 id> 標記(同 tbody 可有多顆鈕)。
    // 純呼叫膠水:bindLoadMore 帶 cursor 發 query.*Page、把回傳列 append 進 tbody、到底移除鈕。
    container.querySelectorAll('[data-load-more]').forEach(function (btn) {
      var id = btn.getAttribute('data-load-more');
      var spec = LOAD_MORE_SPECS[id];
      if (!spec) { return; }
      var detail = btn.closest('.rule-detail');
      var tbody = detail ? detail.querySelector('[data-load-target]') : null;
      if (!tbody) { return; }
      Ui.bindLoadMore(btn, spec.fetchPage, function (rows) {
        Ui.appendRowsToTbody(tbody, rows, spec.columns);
      }, function () {
        // 首擊清掉上方預覽列(ABS-DESC top-50 等),改接 keyset ASC 第一頁,避免重複與排序不一致。
        // 一格 tbody 兩鈕(空傳票號/科目)時只清一次:由先點的鈕清掉混合預覽,
        // 後點的鈕不再清(直接接自己那類),避免清掉前一鈕已載入的列。
        if (tbody.getAttribute('data-cleared') === '1') { return; }
        tbody.innerHTML = '';
        tbody.setAttribute('data-cleared', '1');
      });
    });

    // 「檢視 / 收合」切換。
    // 對 prescreenPreview 類型：首次展開時觸發惰性抓取；之後只切 hidden。
    // 其餘類型（reason / table）：純 DOM，無 API。
    container.querySelectorAll('[data-action="toggle-detail"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var scope = btn.getAttribute('data-scope');
        var idx = btn.getAttribute('data-idx');
        var panel = container.querySelector('[data-bind="rule-detail-' + scope + '-' + idx + '"]');
        if (!panel) { return; }

        panel.hidden = !panel.hidden;
        btn.textContent = panel.hidden ? '檢視' : '收合';
        btn.classList.toggle('is-open', !panel.hidden);

        // 只在展開時、且面板含 prescreenPreview 子區塊時觸發惰性抓取。
        if (panel.hidden) { return; }

        // 從當前 store 狀態取 runId，作為快取鍵的一部分。
        var state = Store.getState();
        var presRun = state.lastRuns.prescreen;
        var runId = presRun && presRun.resultRef ? presRun.resultRef.runId : null;

        panel.querySelectorAll('[data-prescreen-key]').forEach(function (block) {
          var key = block.getAttribute('data-prescreen-key');
          var cacheKey = (runId || 'unknown') + '|' + key;

          // 已快取：直接填入並跳過（prevent re-fetch）。
          if (prescreenPreviewCache[cacheKey]) {
            block.querySelector('.rule-detail__preview-body').innerHTML = prescreenPreviewCache[cacheKey];
            return;
          }

          // 尚未載入：顯示佔位符並發起 filter.preview 請求。
          var bodyEl = block.querySelector('.rule-detail__preview-body');
          bodyEl.textContent = '載入中…';

          Ui.run('載入命中預覽', function () {
            return global.JetApi.filterPreview({ scenario: prescreenScenario(key) }).then(function (data) {
              var s = data.scenario;
              var html =
                '<p class="rule-detail__preview-summary">' + Ui.esc(
                  '命中 ' + Number(s.count).toLocaleString() + ' 筆／' +
                  Number(s.voucherCount).toLocaleString() + ' 張傳票，以下為前 ' +
                  s.previewRows.length + ' 筆'
                ) + '</p>' +
                '<div class="preview-table__wrap">' + Ui.previewTableHtml(s.previewRows) + '</div>';
              prescreenPreviewCache[cacheKey] = html;
              bodyEl.innerHTML = html;
            });
          });
        });
      });
    });

    if (!ready) { return; }

    container.querySelector('[data-action="run-validate"]').addEventListener('click', function () {
      Ui.run('執行驗證', function () {
        return global.JetApi.validateRun({}).then(function (data) {
          Store.setLastRun('validate', data);
          Store.addMessage(
            '資料驗證完成：科目不符 ' + data.completenessTest.diffAccountCount + '、傳票不平 ' +
            data.docBalanceTest.unbalancedDocumentCount + '、抽樣 ' + data.infSamplingTest.sampleSize +
            ' 筆、欄位異常 ' + (Number(data.nullRecordsTest.nullAccountCount) +
              Number(data.nullRecordsTest.nullDocumentCount) +
              Number(data.nullRecordsTest.nullDescriptionCount) +
              Number(data.nullRecordsTest.outOfRangeDateCount)) + ' 筆。', 'info');
        });
      });
    });

    container.querySelector('[data-action="run-prescreen"]').addEventListener('click', function () {
      Ui.run('執行預篩選', function () {
        return global.JetApi.prescreenRun({}).then(function (data) {
          Store.setLastRun('prescreen', data);
          Store.addMessage(
            '風險預篩選完成：期末後核准 ' + data.postPeriodApproval.count +
            '、摘要特定描述 ' + data.suspiciousKeywords.count +
            '、連續零尾數 ' + data.trailingZeros.count +
            '、週末過帳 ' + data.weekendActivity.postingCount +
            '、假日過帳 ' + data.holidayActivity.postingCount + ' 筆。', 'info');
        });
      });
    });
  }

  function mockRunValidation() {
    var state = Store.getState();
    if (!state.mapping.gl.committed || !state.mapping.tb.committed) {
      Store.addMessage('請先完成欄位配對（可用「欄位配對」步驟的測試案件按鈕）。', 'warn');
      return;
    }

    Ui.run('套用測試案件（驗證與預篩選）', function () {
      return global.JetApi.validateRun({}).then(function (v) {
        Store.setLastRun('validate', v);
        return global.JetApi.prescreenRun({});
      }).then(function (p) {
        Store.setLastRun('prescreen', p);
        var v = Store.getState().lastRuns.validate;
        Store.addMessage(
          '驗證與預篩選完成：完整性差異 ' + v.completenessTest.diffAccountCount +
          '、借貸不平 ' + v.docBalanceTest.unbalancedDocumentCount +
          '、摘要特定描述 ' + p.suspiciousKeywords.count +
          ' 筆、假日過帳 ' + p.holidayActivity.postingCount + ' 筆。', 'info');
      });
    });
  }

  Ui.registerStep('validate', render);
  Ui.registerMockLoader('validate', mockRunValidation);
})(window);
