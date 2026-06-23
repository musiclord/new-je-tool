# 多情境逐列 tag 矩陣設計(子專案 D2)

> **狀態:已實作,待 GUI 驗收。** 三個 query action（`tagMatrixScenarios` / `tagMatrixVoucherPage` / `tagMatrixRowPage`）、`idx_result_filter_run_entry` 索引、抽出來的共用服務 `FilterRunMaterializeService`、前端矩陣預覽都已落地;自動化測試全套本機綠（含 SQL Server parity 實跑）。GUI 目視驗收和提交都還沒做。本子專案是「匯出底稿」里程碑下的 **D2**:把 D1 已經落地的篩選命中（`result_filter_run`）組成方法學的 step4（傳票層 C1..CN 布林矩陣）與 step4-1（行層逐行 tag）所需的查詢基礎設施。它依賴 D1（`result_filter_run` 加 Page 原語）、C 與 C 補遺（C5/C6/C9 等 row-tag）、A（情境上限 10）。
>
> **盤查依據:** `.git/sdd/bcde-scope-draft.md`（D2 = 多情境 × 逐列 tag 矩陣，全 milestone 風險最高）;2026-06-21 在 D2 開工前的自我審查，實機重解兩份 WorkingPaper 樣本（step3/step4/step4-1），並對碼確認 `result_filter_run` 的行層命中足以同時導出兩種矩陣（見 `SqliteFilterRunMaterializer.cs`、`GlRulePredicates.cs` 中 C1 註解「tag 落在符合的那些分錄列上」）。

## 背景與動機

事務所方法學的 step4 / step4-1 是 JE 測試的核心產出:

- **step4「符合高風險條件傳票」（傳票層）:** 每列是一張命中至少一條高風險條件的去重傳票。欄位是傳票號、總帳日、編製者、傳票總額，加上 **C1..C10 的命中布林**（這張傳票有沒有命中各條件，Y 或空白），再加手填查核欄。樣本實證:福懋 step4 的 F–O 欄就是 C1..C10。
- **step4-1「符合高風險條件傳票明細」（行層）:** 命中傳票的**每一行** GL 明細。欄位是傳票號、項次、日期、人員、科目、金額、摘要，加上 **逐行的 C*_TAG**（這一行有沒有命中各條件）。樣本實證:同一張傳票裡，只有命中那條件的特定行才有 Y（例如罕用科目 C9 只標在罕用科目的那一行）。

這裡的「C1..CN」只是位置槽，實際內容就是審計團隊存的篩選情境（`config_filter_scenario`，position 從 1 到 N，N ≤ 10）。JET 現況:`filter.preview` 一次只算一個情境，D1 的 `query.filterHitsPage` 一次只回單一 position 的命中行。**目前沒有「把全部情境跑一遍、逐傳票或逐行標出命中了哪些情境」的矩陣**。D2 就是補這個。

## 已驗證的資料地基（D2 開工前自我審查的結論）

`result_filter_run` 的 schema 是 `(scenario_position, entry_id)`，命中**存在 GL 行層**（`filter.commit` 對每個已存情境執行 `INSERT ... SELECT entry_id WHERE {述詞}`）。`GlRulePredicates` 的所有述詞都以單行為單位評估，跨行的條件（C1 借貸組合、account_pair）則用傳票範圍的 EXISTS 子查詢處理。所以這一個行層結構**同時**能導出兩種矩陣:

- **step4 傳票層:** 某傳票命中情境 S，等同於該傳票存在任一行在 `result_filter_run` 裡被標記為 S（用 `EXISTS` 或 `JOIN ... GROUP BY document_number`）。
- **step4-1 行層:** 某行命中情境 S，等同於 `(S, 該行 entry_id)` 存在於 `result_filter_run`（直接判斷成員資格）。

因此 **D2 不需要新的命中儲存，也不需要新的矩陣表**，只要從 `result_filter_run` 即時 pivot 出來就好。

## 設計決策

### 即時算，不落地新矩陣表(Good Taste)

命中已經落地在 `result_filter_run`，而矩陣（pivot）只是一個便宜的 `JOIN` / `GROUP BY` / keyset 查詢。如果另存一張 pivot 後的矩陣表，會**重複資料**、引入新的失效來源，而且可能跟 `result_filter_run` 不一致。所以 D2 **即時從 `result_filter_run` 算矩陣**，沿用 D1 的 Page 原語做 keyset 分頁。這樣永遠和命中一致，也不增加任何失效不變量。

### 每頁兩段查詢（provider 中立，避開方言聚合）

「每張傳票或每一行命中了哪些情境位置」是一對多關係。要在單一 SQL 內把這些位置聚成一欄，得用 `group_concat`（SQLite）或 `STRING_AGG`（SQL Server）這類**各家方言不同**的聚合函式。為了保持 provider 中立、而且讓 keyset 分頁乾淨，採用**每頁兩段查詢**:

1. **實體頁查詢（keyset）:** 取本頁的去重傳票（step4，鍵是 `document_number` ASC）或命中傳票的所有行（step4-1，鍵是 `entry_id` ASC），連同核心顯示欄一起取，用 `ORDER BY 鍵 + Dialect.LimitClause`。這一段回傳本頁的鍵範圍 `(@cursor, @lastKey]`。
2. **位置查詢:** 對**同一個鍵範圍**取出 `(實體鍵, scenario_position)`，在 handler（C#）端分組，組成每個實體的「命中位置有序去重清單」。

兩段都用參數綁定、都是純 ANSI（只有 LimitClause 走方言），而且每頁有界（最多 pageSize 個實體 × 最多 10 個位置）。把位置對映到 C1..CN 欄是 E（writer）的事。

### 輔助索引（加法，不升版）

`result_filter_run` 的 PK 是 `(scenario_position, entry_id)`，`entry_id` 不是前導欄，所以「用 entry_id join 回 target_gl_entry 算傳票」和「行頁的位置查詢 `WHERE entry_id` 範圍」都拿不到最佳索引。因此新增 `idx_result_filter_run_entry ON result_filter_run(entry_id, scenario_position)`（雙 provider，用 `IF NOT EXISTS` / `IF ... IS NULL`，**不升 schema 版本**，跟 D1 的加法建表慣例一致）。`target_gl_entry.document_number` 本來就有索引，傳票分組 join 不會慢。

### 惰性 materialize（沿用 D1）

矩陣必須反映全部已存情境。沿用 `query.filterHitsPage` 的惰性補算:第一次查詢若結果為空（或 summary 全是 0）而 `config_filter_scenario` 又有定義，就重用 `IFilterRunMaterializer` 把全部情境落地，然後重取一次。為了不重複實作，把 `filterHitsPage` 既有的私有 `MaterializeAllAsync` 抽成共用的 Application 服務 `FilterRunMaterializeService`，由 `filterHitsPage` 和 D2 的三個 handler 共用（DRY，單一事實來源）。

## 目標與範圍

### 做

1. **`query.tagMatrixScenarios`**（矩陣表頭 / step3 交叉參考):回傳全部已存情境的 `position`、`name`，加上**傳票層命中數**（`COUNT(DISTINCT document_number)`）和**行層命中數**（`COUNT(*)`），依 position 排序;命中數為 0 的情境也要列出（count=0）。含惰性 materialize 守門。
2. **`query.tagMatrixVoucherPage`**（step4 傳票層):keyset 分頁的去重命中傳票，每列是 `documentNumber` / `postDate` / `createdBy` / `voucherTotal`（傳票總額，scaled 整數）加上 `matchedPositions`（命中位置的有序去重清單）。鍵是 `document_number` ASC。
3. **`query.tagMatrixRowPage`**（step4-1 行層):keyset 分頁命中傳票的**所有行**，每列是 `documentNumber` / `lineItem` / `postDate` / `approvalDate` / `createdBy` / `approvedBy` / `accountCode` / `accountName` / `amount`（signed scaled）/ `description`，加上 `matchedPositions`（這一行命中的位置;非命中行則是空清單）。鍵是 `entry_id` ASC。
4. **前端「高風險條件矩陣」預覽**:情境摘要（C1..CN 名稱加命中數）、傳票矩陣（載入更多）、點傳票展開它的行層 tag（載入更多）。沿用 D1 的載入更多基礎設施，零商業邏輯。

### 不做（移交給別人 / 暫時不需要）

- **不做匯出 writer**（把 step4/step4-1 寫進 .xlsx、C*_TAG 動態欄位集、傳票總額格式）— 屬於 E。D2 只回矩陣資料。
- **不另存 pivot 矩陣表，也不改 `result_filter_run` schema**（只加索引）。
- **不改既有規則述詞、`filter.commit`、`filterHitsPage` 的命中語意**;D2 純讀 `result_filter_run`。
- **不做 step4-1 的動態欄位決策**（哪些 C*_TAG 欄要出現）— 屬於 E;D2 回完整的位置清單。
- 不新增規則或情境型別（A、C、C 補遺已經把 row-tag 備齊了）。

## 開發原則與邊界(harness — 本 spec 的 Global Constraints)

- **分層**:`Bridge/Form1 → Application → Domain ← Infrastructure`。矩陣列型別和 Page 原語放 Domain;**provider 分支只能在 Infrastructure**（SELECT 骨架加方言）;Application 的 handler 負責編排（惰性 materialize、scaled 轉顯示值）。
- **前端零商業邏輯**:前端、Form1、HTML、CSS、JS 都不做查詢、pivot、SQL、計算，只負責顯示和組 wire。
- **postMessage 邊界**:`window.chrome.webview.postMessage` 只能在 `jet-api.js` 裡呼叫。
- **查詢一律是參數化 SQL**:識別字只能來自常數或白名單;游標、position、鍵範圍一律參數綁定;用 `DbCommand` 保持 provider 中立;方言不同的片段走 `ISqlDialect`（LimitClause / ParameterName）。
- **keyset 分頁**:用展開布林式游標（`鍵 > @cursor`），不用 OFFSET，避免大表退化。排序鍵要唯一且有索引（去重傳票用 `document_number` 為鍵以確保穩定排序;行頁用 `entry_id` 為鍵，PK 唯一）。游標 opaque（Base64，`PageCursor`）;壞游標要 fail loud（回 `invalid_payload`，沿用 D1）。
- **契約先行**:動 wire 形狀前**先改 `docs/action-contract-manifest.md`**（三個新 query action 加 facade）;`ActionContractTests` 鎖住回應形狀。
- **雙 provider 等價**:SQLite 和 SQL Server 兩邊同步;每個 Page 或 summary 查詢都**比照 D1 加 `[SqlServerFact]` parity**（雙 provider 走訪等價、count 等價;沒設 LocalDB 就乾淨跳過，不是 sqlite-only）。
- **不破壞失效不變量**:`result_filter_run` 隨 `RuleRunResultReset` 失效（D1 已含）;D2 純讀，沒有新的失效來源;新增的索引隨表存在。
- **測試分三層(jet-testing 規範)**:Domain（Page 原語沿用既有的;新增矩陣列型別）;Application 驗收（用 `HandlerTestHost` 對 manifest wire：summary count 等於獨立 recount、voucher/row 矩陣的命中位置等於獨立 recount、惰性 materialize、壞游標回 invalid_payload）;Infrastructure/parity（真 SQLite 加 SQL Server LocalDB 閘控，走訪等價）。
- **採 TDD**;**不自行 commit**（版本控制由使用者親自下令，subagent 執行階段也不 commit，用 tree-diff 隔離）;所有 subagent 用 `claude-opus-4-8`、effort max。

---

## 動作契約(摘要;細節進 manifest)

### `query.tagMatrixScenarios`
- payload:無（用 session 的 projectId）。
- response:`{ scenarios: [ { position, name, voucherHitCount, rowHitCount } ], … }`，依 position 升冪。
- 惰性 materialize:全部 count 都是 0 但有已存情境時，落地後重算一次。

### `query.tagMatrixVoucherPage`
- payload:`{ cursor?, pageSize? }`（壞 cursor 回 invalid_payload;pageSize 預設 200、上限 500）。
- response:`{ rows: [ { documentNumber, postDate, createdBy, voucherTotal, matchedPositions:[int] } ], nextCursor }`。
- `voucherTotal` = 該傳票的 `SUM(debit_amount_scaled)`（傳票借方總額;scaled 轉 decimal 的顯示換算由 handler 做;對齊樣本 step4「傳票總金額」是毛額正數）。
- 鍵是 `document_number` ASC;惰性 materialize（首頁空且有情境就落地重取）。

### `query.tagMatrixRowPage`
- payload:`{ cursor?, pageSize? }`。
- response:`{ rows: [ { documentNumber, lineItem, postDate, approvalDate, createdBy, approvedBy, accountCode, accountName, amount, matchedPositions:[int], description } ], nextCursor }`。
- 列集 = 命中傳票（任一行進了 `result_filter_run`）的**所有行**;`matchedPositions` 是該行（entry_id）直接命中的位置（非命中行為 `[]`）。
- 鍵是 `entry_id` ASC;惰性 materialize。

## 受影響的現行碼與新增(盤查)

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:三 query action + facade 對照 + 細節段 |
| `Domain/PageRows.cs`(或新檔) | 新增 `VoucherTagRow`、`RowTagRow`、`ScenarioTagSummary` 列型別 |
| `Infrastructure/{Jet,SqlServer}ProjectDatabase.cs` | 加 `idx_result_filter_run_entry(entry_id, scenario_position)`(IF NOT EXISTS,不升版) |
| `Application/FilterRunMaterializeService.cs`(新) | 提取 filterHitsPage 的 MaterializeAllAsync 為共用服務 |
| `Application/QueryFilterHitsPageHandler.cs` | 改用共用服務(DRY,語意不變) |
| `Domain/I*TagMatrix*Repository.cs`(三介面)+ `Infrastructure/{Sqlite,SqlServer,ProviderRouting}*` | summary + voucherPage + rowPage 三組查詢(兩段查詢、keyset、方言 LimitClause) |
| `Application/QueryTagMatrix{Scenarios,VoucherPage,RowPage}Handler.cs`(新) | 編排 + 惰性 materialize + scaled 換算 |
| `AppCompositionRoot.cs` | 註冊三 handler + 三 repo + 共用服務 |
| `wwwroot/js/jet-api.js` + `steps/*` + css | 三 action 字串 + 矩陣預覽(載入更多;零邏輯) |
| `docs/jet-guide.md`/`development-status.md`/`development-log.md`/`windows-handoff.md` | 回寫 + 待驗卡 |

## 新增/更新測試(TDD,三層)

- **Domain**:矩陣列型別是位置式記錄（欄序對齊 wire）;Page 原語沿用既有測試。
- **Application 驗收**（用 `HandlerTestHost`，對 manifest wire）:
  - `tagMatrixScenarios`:各情境 voucherHitCount 要等於 `COUNT(DISTINCT document_number)` 的獨立 recount、rowHitCount 要等於 `COUNT(*)` 的 recount;命中數 0 的情境也要列出;惰性 materialize（沒先跑 commit 也能算）。
  - `tagMatrixVoucherPage`:命中傳票集要等於獨立 recount;每傳票的 matchedPositions 要等於 recount（該傳票命中的 distinct position）;voucherTotal 要等於 SUM(debit) recount;走訪全部頁不重複、單一升冪;壞 cursor 回 invalid_payload。
  - `tagMatrixRowPage`:列集要等於命中傳票的所有行（含非命中行）recount;每行的 matchedPositions 要等於 recount（該 entry_id 命中的 position;非命中行為空）;走訪等價;壞 cursor。
- **Infrastructure/parity**:三個查詢在 SQLite 與 SQL Server 上 `[SqlServerFact]` 走訪等價（鍵序列 SequenceEqual 加 matchedPositions 等價;用 `Count > pageSize` 擋偽綠;沒設連線就乾淨跳過）。
- **前端**:不寫 JS 業務測試;矩陣預覽和載入更多列進 windows-handoff。

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需本機 LocalDB 並設 `JET_SQLSERVER_CONNECTION`(閘控)。

## Windows 端待驗任務(落地時寫入 windows-handoff.md)

- 「進階條件篩選」（或新檢視）出現「高風險條件矩陣」:情境摘要列出 C1..CN（名稱加傳票/行命中數）;傳票矩陣每列顯示命中了哪些 C（載入更多逐頁）;點一張傳票可展開它的行層明細與逐行 tag（載入更多）。
- 多情境（存滿到 10 個）時矩陣欄位要正確對位;大母體逐頁要流暢、不爆版;沒存任何情境時要有友善的空狀態。

## 與其他子專案的邊界

- **D1**:沿用 Page 原語（PageCursor/PageRequest/PageResult）、`result_filter_run`、惰性 materialize;D2 即時 pivot，不另落地。
- **C / C 補遺 / A**:矩陣的情境來自使用者已存的（可含 C5/C6/C9、backdated、週末、假日等任何 row-tag）;D2 不限定條件種類。
- **E（最後一棒）**:E 把 D2 的矩陣資料寫進 .xlsx 的 step4/step4-1（C*_TAG 動態欄位集、傳票總額格式、手填欄留空、step3 定義表）;D2 只提供查詢。
