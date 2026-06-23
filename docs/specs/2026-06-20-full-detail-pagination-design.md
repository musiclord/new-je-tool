# 全量明細基礎設施設計(子專案 D1)

> **狀態:已實作,待 GUI 驗收。** 程式碼與自動化測試都已完成(本機 588 個測試通過,SQL Server 雙資料庫等價測試也實跑過)。GUI 上有五處「載入更多」還等使用者手動驗收。本子專案是「匯出底稿」里程碑底下的 **D1(全量明細基礎設施)**。權威描述已經搬到 `docs/jet-guide.md`(§13 全量明細 keyset 分頁)、`docs/action-contract-manifest.md`、`docs/development-status.md`。GUI 驗收通過後,本快照依慣例去掉日期前綴或刪除。
>
> **盤查依據:** `.git/sdd/bcde-scope-draft.md`(把原本的子專案 D 拆成 D1 與 D2)。效率取捨與雙資料庫(provider)的實作細節都查過 Microsoft Learn 文件,出處見後面「分頁機制」一節。

## 背景與動機

匯出底稿時需要拿到「全部」明細,但 JET 目前每個明細查詢都只回前 50 筆(`LIMIT 50` / `TOP (50)`),篩選命中的結果不會存進資料庫,INF 抽樣也只回筆數加亂數種子、沒辦法把抽中的那幾列讀回來。D1 補上「把全量資料一次次取完」的基礎設施,讓後面的 D2(多情境 tag 矩陣)和 E(匯出 writer)能用,同時也讓 GUI 既有的明細檢視可以「載入更多」。D1 本身不做 tag 矩陣(那是 D2),也不做匯出 writer(那是 E)。

目前共有五個地方會把結果截斷到 50 筆,這是驗證員核實過的:全科目完整性差異、借貸不平、空值紀錄、篩選命中(`filter.preview` 最多 50 筆)、INF 抽樣明細(根本沒有讀回的路徑)。

## 目標與範圍

### 做

1. **keyset 游標分頁查詢**:新增五個 `query.*Page` action,涵蓋完整性差異、借貸不平、空值紀錄、篩選命中、INF 抽樣明細。每個都支援把全量走完(回一頁資料加一個指向下一頁的游標)。
2. **把篩選命中結果落地到 `result_filter_run`(精簡參照)**:已保存的篩選情境(C1–C10)命中哪些 `entry_id`,持久化進這張表。`filterHitsPage` 從這張表讀,再 join 回 `target_gl_entry` 取完整欄位。
3. **INF 60 行明細回取**:`infSamplePage` 讀已存的 `result_inf_sampling_test_sample`,join 回 target,回傳 step2 需要的欄位(借方、貸方拆成兩欄、過帳日、核准日、編製人員、核准人員)。
4. **GUI「載入更多」**:五個明細檢視都加上游標式分頁,點一下接續載入下一頁,不是整表重載。

### 不做(移交給別人 / 暫時不需要)

- **多情境 × 逐列的 tag 矩陣**(step4 傳票層布林、step4-1 行層的 C*_TAG)移交給 D2。D1 只負責「單一情境命中的全量明細」以及把它落地。
- **匯出 writer、`*_JE` 正準名對照**移交給 E。
- **跳到任意頁碼(random-access offset 分頁)**。keyset 本來就不支援任意跳頁。評估後發現底稿匯出和 GUI 審閱都只需要「往下一頁前進」,所以這功能現在用不到,不做。
- 既有 `validate.run` 和 `filter.preview` 的「最多 50 筆快速預覽」保持不動。互動審閱仍然用它;Page 系列另外負責全量走訪,兩者職責分開。

## 開發原則與邊界(harness — 本 spec 的 Global Constraints)

- **分層**:`Bridge/Form1 → Application → Domain ← Infrastructure`。游標的編碼解碼和契約型別屬於 Domain;SQL 和分頁述詞屬於 Infrastructure;**provider(資料庫廠商)分支只能出現在 Infrastructure**。
- **前端零商業邏輯**:前端、Form1、HTML、CSS、JS 都不准做 SQL、計算或命中判定。「載入更多」只能呼叫 Page action,然後把回傳的列接到既有清單後面。
- **postMessage 邊界**:`window.chrome.webview.postMessage` 只能在 `jet-api.js` 裡呼叫,別處不行。
- **查詢一律是參數化的集合式 SQL**:識別字只能來自白名單或常數,游標值和頁大小一律用參數綁定。用 DbCommand,保持 provider 中立,方言差異走 `ISqlDialect`。
- **契約先行**:新的 `query.*Page` action 形狀**要先寫進 `docs/action-contract-manifest.md`** 再動程式。ActionContractTests 會鎖住回應形狀。
- **雙 provider 等價**:SQLite 和 SQL Server 兩邊同步。**游標述詞要用展開的布林式(見下),不可用元組比較**,因為 SQL Server 不支援元組比較。SQL Server 測試走 LocalDB,沒設連線就乾淨跳過。
- **結果失效要保持一致**:`result_filter_run` 要跟既有的失效集一起連動。當重投影(清掉 `target_gl_entry`)時也要一併清掉它,做法併進 `RuleRunResultReset.ClearWithinAsync`,跟子專案 B 處理 `gl_control_total` 的模式一樣。這不能破壞 `result_rule_run` 的回放。
- **不編輯** WinForms designer 產生的檔案。
- **測試分三層(jet-testing 規範)**:Domain 層做純函式單元測試(游標編碼解碼、頁大小夾擠);Application 層做驗收測試(用 `HandlerTestHost` 對 manifest 的 wire 驗證:分頁走訪到底不漏不重、result_filter_run 命中正確、INF 明細欄位正確);Infrastructure 層做 parity 測試(真 SQLite,SQL Server 走 LocalDB 閘控)。
- **採 TDD**;**不自行 commit**。版本控制由使用者親自下令,subagent 執行階段也不 commit,改用 tree-diff 隔離。

---

## 分頁機制(keyset 游標)— 效率與雙 provider 的核心

### 為何用 keyset 而非 OFFSET/FETCH

Microsoft Learn 的 EF Core Pagination 與 T-SQL ORDER BY OFFSET/FETCH 文件講得很清楚:OFFSET 分頁要資料庫「先把被略過的列處理一遍再丟掉」,略過越多越慢,而且資料同時被改動時還會跳列或重複。keyset(又稱 seek)分頁則是在 `WHERE` 裡帶上「上一頁最後一列的鍵」往後跳,有索引時非常快,對並發也穩定。JET 的母體有 140 萬列,所以一律用 keyset。

### 跨 provider 的游標述詞:用展開布林式,不可用元組

多數資料庫(包括 SQLite)支援 row-value 元組比較,也就是 `WHERE (k1, k2) > (@k1, @k2)`,但 **SQL Server(T-SQL)不支援**(MS-TSQLISO F641 把 row/table constructor 列為「limited support」)。所以 D1 的游標述詞**一律寫成展開的布林式**,兩個 provider 共用同一份:

```sql
-- 單鍵游標(entry_id / account_code / document_number):
WHERE <key> > @cursorKey
-- 多鍵游標(若日後需要 k1 排序、k2 tiebreaker):
WHERE k1 > @c1 OR (k1 = @c1 AND k2 > @c2)
```

取一頁的筆數用各 provider 既有的方言:SQLite 用 `LIMIT @pageSize`,SQL Server 用 `ORDER BY ... OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY`。注意這裡 OFFSET 永遠是 0,只是借它的語法做 TOP-N,並不是真的 offset 分頁。limit 子句由 `ISqlDialect` 產生。

### 排序鍵的唯一性與索引(必要條件)

- **排序鍵必須唯一**,否則翻頁會漏或重複。每個查詢都採用天然唯一的鍵(見下表)。
- **每個游標鍵都要有對應索引**,seek 才不會退化成全表掃描。`target_gl_entry` 現有的索引是:PK `entry_id`、`ix_..._account(account_code)`、`ix_..._doc(document_number)`。`result_filter_run` 以 `(scenario_position, entry_id)` 為 PK,天然覆蓋。不足的索引本輪補上。

### 五個分頁查詢的鍵與排序

| Page action | 資料 | 排序/游標鍵(唯一) | 索引 |
|:--|:--|:--|:--|
| `query.completenessDiffPage` | TB+GL 逐科目差異(diff≠0) | `account_code` ASC | 既有 account 索引 |
| `query.docBalancePage` | 借貸不平傳票(GROUP BY 傳票 HAVING 淨額≠0) | `document_number` ASC | 既有 doc 索引 |
| `query.nullRecordsPage` | 空值紀錄(分類:日期區間外/空白摘要/空白傳票號·科目) | `entry_id` ASC(+ 分類參數) | PK |
| `query.filterHitsPage` | 單一已存情境命中(行層) | `entry_id` ASC | result_filter_run PK |
| `query.infSamplePage` | INF 抽樣 60 行明細 | `entry_id` ASC | PK + sample PK |

> 注:`completenessDiffPage` 走全量時用 `account_code` ASC(可索引、唯一、穩定);既有的最多 50 筆預覽則維持 `ABS(diff) DESC`(最大差異優先)。兩者語意刻意不同:預覽要看重點,Page 要走全量,職責分離。

### 游標契約(opaque)

- Page 回應的形狀是 `{ rows: [...], nextCursor: string|null }`。`nextCursor` 是一個 opaque 字串,由 Domain 純函式把末列的鍵編碼而成;`null` 表示已經到底。請求首頁時傳 `cursor: null`。
- 頁大小:`pageSize` 預設 **200**、上限 **500**(由 Domain 夾擠),避免一次拉太多。
- 游標的編碼解碼是 Domain 純函式,可單元測試:編碼時把末列的鍵轉成字串,解碼時轉回強型別的鍵;格式不對就回錯,讓 handler 報參數錯誤,不靜默吞掉。

---

## `result_filter_run` 落地(精簡參照)

- **這是一張新表**(雙 provider,隨基底 schema 用 `IF NOT EXISTS` 建立,不需要升 schema 版本,跟 `app_message_log` 和 `gl_control_total` 先前的做法一樣):

  ```sql
  CREATE TABLE IF NOT EXISTS result_filter_run (
      scenario_position INTEGER NOT NULL,
      entry_id          INTEGER NOT NULL,
      PRIMARY KEY (scenario_position, entry_id)
  );
  ```

  它只存「哪個情境命中哪些 `entry_id`」這種行層參照。傳票層的命中則由 `filterHitsPage` 取 distinct `document_number` 得出。**它不存去正規化的整列資料**,因為那會造成重複又容易失同步。
- **何時落地**:在 `filter.commit`(保存情境)時,用既有的 `GlFilterWhereBuilder` 把該情境的 AST 組成命中述詞,把命中的 `entry_id` 寫進 `result_filter_run`。同一個 `scenario_position` 先刪後插,保證冪等。情境被刪除或覆寫時,同步刪掉該 position 的列。
- **惰性補算**:`filterHitsPage` 讀取時,如果該 position 沒有落地的列(例如 D1 上線前就保存的舊情境),就當場用同一份述詞補算並落地,然後才回傳。這讓查詢更穩健,而且只會發生一次。
- **失效**:重投影清掉 `target_gl_entry` 時,在 `RuleRunResultReset.ClearWithinAsync` 裡一併執行 `DELETE FROM result_filter_run`(同一個交易),確保命中參照永遠不會指向已經失效的母體。

## INF 60 行明細回取

- `infSamplePage` 讀既有的 `result_inf_sampling_test_sample`(裡面已經存了抽樣的 `entry_id`),join `target_gl_entry`,以 `entry_id` ASC 做 keyset 走訪。
- 回傳 step2 需要的欄位:傳票號、科目編號與名稱、**借方金額與貸方金額(由 signed amount 拆成兩欄)**、過帳日、核准日、編製人員、核准人員、摘要。金額用既有的 `debit_amount_scaled` / `credit_amount_scaled` 輸出,顯示層再做 ToDisplay。
- 不重新抽樣,因為種子已經固定落地;這裡只負責讀回。

## GUI「載入更多」(效率與不卡頓)

- 五個明細檢視(完整性差異、借貸不平、空值、篩選情境命中明細、INF 抽樣)都在既有的「最多 50 筆預覽」下方加一顆「載入更多」鈕。
- 點下去的行為:帶著當前的 `nextCursor` 呼叫對應的 `query.*Page`(經 `jet-api`),把回傳的 `rows` **接在**現有列後面,然後更新 `nextCursor`;當 `nextCursor` 是 `null` 就隱藏這顆鈕(表示已經到底)。
- **不卡頓**:游標增量載入,不整表拉取。載入期間鈕顯示載入中狀態(disabled 加旋圈),非同步進行,不凍結 UI。沿用 `minimalist-ui` 既有的清單樣式,不另外加重樣式。
- **前端零邏輯**:前端不判命中、不算差異、不組 SQL,只負責發 action、接列、管游標和載入狀態。

## 受影響的現行碼與新增(盤查)

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:5 個 `query.*Page` action(請求 `{cursor,pageSize,...}`、回應 `{rows,nextCursor}`)、各 row 形狀 |
| `Domain/`(新)`PageCursor` 編解碼 + `PageRequest`/`PageResult<T>` 契約 + pageSize 夾擠常數 | keyset 游標純函式 |
| `Domain/` 各 Page 的 row record(完整性/借貸不平/空值/篩選命中/INF 明細) | 強型別回傳 |
| `Infrastructure/{Sqlite,SqlServer}` 各 Page 查詢倉儲 | keyset SQL(展開布林式 + 方言 limit);雙 provider |
| `Infrastructure/{Jet,SqlServer}ProjectDatabase.cs` | `result_filter_run` 建表(IF NOT EXISTS / IF OBJECT_ID);必要索引 |
| `Infrastructure/RuleRunResultReset.cs` | 失效集加 `DELETE FROM result_filter_run` |
| `Infrastructure/`(filter.commit 落地路徑) | 保存情境時 materialize 命中 entry_id |
| `Application/` 5 個 Page handler + filter.commit 擴充 | 對 manifest wire |
| `Application/` INF handler | infSamplePage |
| `wwwroot/js/steps/*`、對應 css | 五處「載入更多」(零邏輯) |
| `docs/jet-guide.md` / `development-status.md` / `development-log.md` | 落地回寫 |

## 新增/更新測試(TDD,三層)

- **Domain**:`PageCursor` 編碼後再解碼要 round-trip 回原值(entry_id / account_code / document_number);壞游標字串要回錯;`pageSize` 夾擠正確(0 或負數回預設、大於 500 變 500)。
- **Application 驗收**(用 `HandlerTestHost`,對 manifest wire):
  - 各 Page action 把分頁走訪到底:逐頁串接起來要等於一次性取出的全量集合(**無漏、無重、order 穩定**);末頁的 `nextCursor` 要是 null。
  - `result_filter_run`:`filter.commit` 後該情境命中的 entry_id 落地正確(拿 demo 獨立 recount 比對);`filterHitsPage` 行層結果要等於 recount,傳票層取 distinct doc 也要正確;對舊情境的惰性補算要成立。
  - `infSamplePage`:回 60 行(或實際樣本數),借貸拆欄、日期與人員欄都正確。
  - 失效:重投影後 `result_filter_run` 要被清空(跟 gl_control_total 不變量的測試模式一樣)。
- **Infrastructure/parity**:每個 Page 查詢在 SQLite 和 SQL Server 上要逐頁等價(同游標給出同一頁、同一個 nextCursor);游標的展開布林式述詞在兩個 provider 上要算出相同結果(LocalDB 閘控)。
- **前端**:不寫 JS 業務規則測試(這是邊界);「載入更多」屬於 GUI 行為,列進 windows-handoff 做人工驗收。

## 交付物

| 檔案/位置 | 變更 |
|:--|:--|
| `docs/action-contract-manifest.md` | 5 個 query.*Page + filter.commit 落地說明 |
| `src/JET/JET/Domain/`(新增 PageCursor / PageRequest / PageResult / row records) | 游標與契約 |
| `src/JET/JET/Infrastructure/{Sqlite,SqlServer}*`(Page 查詢、result_filter_run 建表/落地/失效、索引) | keyset SQL 雙 provider |
| `src/JET/JET/Application/`(5 Page handler、filter.commit 擴充、INF) | wire |
| `src/JET/JET/wwwroot/js/steps/*` + css | 五處「載入更多」 |
| `src/JET/tests/JET.Tests/...` | 上述三層測試 |
| `docs/jet-guide.md`、`docs/development-status.md`、`docs/development-log.md` | 落地回寫 |

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需本機 LocalDB 並設 `JET_SQLSERVER_CONNECTION`(閘控)。

## Windows 端待驗任務(落地時寫入 windows-handoff.md)

- 五個明細檢視(完整性差異、借貸不平、空值、篩選情境命中明細、INF 抽樣)都出現「載入更多」,點擊接續載入下一頁,到底時隱藏,載入中不卡頓。
- 在大母體(140 萬列)下載入要流暢,不能整表卡住。

## 與其他子專案的邊界

- **D2**:用 D1 的 `result_filter_run`(單情境命中)組「每傳票/每行 × C1–C10」的 tag 矩陣;D1 自己不做矩陣。
- **E**:用 D1 的 Page 系列走訪全量,寫出 15 張表(step1 全科目、step2 INF 60、step4/4-1 命中明細)。
- **A/B**:已完成;B 的回溯 row-tag 可以當作其中一個情境,它的命中經 D1 落地。
- **方法學**:INF 至少 60 個樣本(已固定);本輪只負責回取,不改抽樣。
