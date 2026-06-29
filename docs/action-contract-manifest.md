# JET Frontend Action Contract Manifest

這份文件是 JET 前端、WebView2 bridge 與 C# handler 三者之間 action 契約的唯一事實來源（source of truth）。任何 agent 在生成 HTML 與 UX、或修改任何 action 之前，都要先讀過這份文件。

## 初步架構階段狀態

有三組能力已經從探索階段畢業、進入正式契約階段：專案持久化（project persistence）、檔案匯入（file import）、欄位配對（mapping）。它們的契約由下方的「Project Persistence / Host / Dev Actions」章節，以及既有的 Project、Import、Mapping 章節定義。

早期還有一組以 `__jet.*` 命名的內部臨時 action（`__jet.probe`、`__jet.projectConfig`），連同它們存放在 %LOCALAPPDATA% 的 prototype blob 資料庫，現在都已退役移除。這組 action 原本只是架構驗證期的鷹架（scaffolding）。移除它們的理由是：它們會讓組態持久化多開一條旁路，這條路徑與正式的專案儲存毫無關係，徒增混亂。退役之後有兩項後果。第一，前端啟動時的 round-trip 連線檢查改走正式的 `system.ping`。第二，專案組態一律持久化在 `projects/{projectId}/` 之下，也就是 `project.json` 這個檔案加上 `jet.db` 裡的 `config_*` 系列資料表；除此之外不存在任何其他組態儲存點。

驗證（`validate.run`）、預篩選（`prescreen.run`）與進階篩選（`filter.preview` / `filter.commit`）也都已進入正式契約階段，各自的細節見對應章節。至於明細分頁的 `query.*Page` 系列與匯出（export）等其餘 action，目前仍是規劃中的契約，動手實作前要先回到這份 manifest 確認形狀。

## 使用原則

1. 前端要優先重用既有的 action，不要自行發明新的 action。
2. 如果 UI 確實需要新的資料或新的行為，順序是先更新這份 manifest，再去修改 `ActionDispatcher` 與相關的 handler。
3. `docs/jet-template.html` 生成 UI 時，必須服從這份文件定義的 action 名稱、payload 形狀、response 形狀，以及固定的 binding 假設。
4. `Bridge` 只負責傳輸（transport），不做任何業務判斷。
5. 業務語意由 `docs/jet-guide.md` 定義，而跨前端、bridge、handler 的資料契約由本文件定義。兩份文件如果牴觸，先回報並修正文件，不要在 UI 或 C# 裡擅自發明新的 action。

## Bridge Envelope

前端送到 WebView2 bridge 的標準請求：

```json
{
  "requestId": "<uuid>",
  "action": "<namespace.action>",
  "payload": {}
}
```

Bridge 回傳的標準回應：

```json
{
  "requestId": "<uuid>",
  "ok": true,
  "data": {},
  "error": null
}
```

失敗時：

```json
{
  "requestId": "<uuid>",
  "ok": false,
  "data": null,
  "error": {
    "code": "<error_code>",
    "message": "<human_readable_message>"
  }
}
```

## Host→Web 事件（Event Envelope）

除了一來一回的 request/response，host 也能主動推播事件給前端。這類事件是單向通知，前端收到後不需要回覆。事件信封與 response 信封的差別在於：事件信封沒有 `requestId`，改以 `event` 欄位辨識。這個設計讓不認識事件形狀的舊前端能安全忽略它，因為前端的 `receive()` 只處理帶 `requestId` 的訊息。事件信封形狀如下：

```json
{
  "event": "<namespace.event>",
  "data": {}
}
```

- 事件的發出與送達分屬兩層。Application handler 透過 `IJetEventPublisher` 這個 port 發出事件；Bridge 的 `WebViewEventPublisher` 接手負責 WebView2 的執行緒 marshal 與 JSON 序列化，它在 UI 執行緒派送，並保證依發出順序送達。如果此時 WebView 還沒就緒，事件會被靜默丟棄。這樣處理是因為事件只是 UX 提示，它不承載狀態權威；真正的狀態權威一律以 action 的 response 為準。
- 前端在 `jet-api.js` 裡用 `JetApi.on(eventName, handler)` 訂閱、用 `JetApi.off(eventName, handler)` 取消訂閱。傳輸細節同樣只存在於 `jet-api.js` 之內。
- 事件不得夾帶資料列，這條約束與 §1.5.4 對 bridge 的約束相同。

| Event | Data | 用途 |
|:---|:---|:---|
| `import.progress` | `{ kind: "gl"\|"tb", fileName, sheetName\|null, rowsRead }` | **Implemented**。`import.gl.fromFile` / `import.tb.fromFile` 串流寫入期間，每讀滿 20,000 列發送一次（`rowsRead` = 該來源累計已讀列數）。**沒有完成事件**：匯入完成以該 action 的 response 為準。前端可用 `rowsRead` ÷ `import.inspectFile` 的 `rowCountEstimate` 顯示近似進度；估計值缺席（CSV）時顯示已讀列數即可 |

## Current Action Registry

### Shell / Bootstrap

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `system.ping` | `{}` | `{ message, utcNow, devToolsEnabled }` | **Implemented**。基本 host 通訊檢查；前端啟動 round-trip 以此為準（取代已退役的 `__jet.probe`）。`devToolsEnabled` 標示本組建是否註冊開發者工具（Debug 組建 true、Release 組建 false）；前端據此決定是否顯示開發面板 |
| `system.databaseInfo` | `{}` | `{ sqlServer: { configured, reachable, server, database, edition, productName, productVersion, engineEdition, isExpress, detail, summary } }` | **Implemented**。回報本組建設定的 **SQL Server 後端身分**（去敏，**永不含密碼或整段連線字串**），供前端在「狀態與訊息」面板顯示「目前連到哪一台／哪個版本／是否 Express」。`configured`＝是否設定了 SQL Server 連線（環境變數 `JET_SQLSERVER_CONNECTION` 或 `Sql:*` 且伺服器非空；SQLite-only 使用者為 false）。`configured` 為 true 時連 **master** 探測（不連單庫，避免單庫尚未建立而誤判）並回 `reachable`＋`edition`（`SERVERPROPERTY('Edition')`，如 `Developer Edition (64-bit)`／`Express Edition (64-bit)`）、`productName`（自 `@@VERSION` 解析，如 `Microsoft SQL Server 2022`）、`productVersion`（如 `16.0.1180.1`）、`engineEdition`（int，`4`＝Express）、`isExpress`（`engineEdition==4` 或 edition 含 "Express"）。`server`＝連線目標伺服器；`database`＝單庫名（`Sql:Database` 或預設 `JET_DEV`）。`reachable` 為 false 時 `detail` 為去敏失敗原因（例 `SqlException Number=...`，不含密碼）。`summary` 為後端組好可直接顯示的中文摘要句（前端不另組業務文字）。此 action **無副作用**（只讀 server 屬性、不建庫不寫入）；探測逾時設短（5 秒），失敗一律收斂、不丟例外、不阻斷啟動。SQLite-only 使用者得到 `configured:false` 與「僅使用本機 SQLite」摘要 |
| `app.bootstrap` | `{}` | `AppBootstrapDto` | Planned。啟動 shell、顯示 DB provider 與 supported actions |
| `project.loadDemo` | `{}` | `DemoProjectDto`（見下方；metadata + mapping，不含 rows） | **Implemented**。載入 deterministic 測試案件 metadata（MockDataLoader 資料來源） |
| `demo.exportGlFile` | `{}` | `{ filePath, fileName }` | **Implemented**。將 deterministic demo GL（2,000 列、2025 年度、金額＋借方旗標模式）寫成 xlsx；前端接 `import.gl.fromFile` |
| `demo.exportTbFile` | `{}` | `{ filePath, fileName }` | **Implemented**。將 deterministic demo TB（100 科目，借貸合計由 demo GL 推導）寫成 xlsx；前端接 `import.tb.fromFile` |
| `demo.exportAccountMappingFile` | `{}` | `{ filePath, fileName }` | **Implemented**。將 deterministic demo 科目配對表（demo GL 科目 → 標準化分類，含 Revenue 與 Receivables/Cash 類）寫成 xlsx；前端接 `import.accountMapping.fromFile` |
| `demo.exportAuthorizedPreparerFile` | `{}` | `{ filePath, fileName }` | **Implemented**。將 demo 授權編製人員清單寫成單欄 .xlsx，供測試案件走 `import.authorizedPreparer.fromFile` 匯入。鏡射 `demo.exportAccountMappingFile` |
| `demo.fetchGlRows` | `{}` | `{ fileName, rows, columns }` | **Legacy compatibility only**；正式 demo/test path 不得使用，改用 `demo.exportGlFile` → `import.gl.fromFile` |
| `demo.fetchTbRows` | `{}` | `{ fileName, rows, columns }` | **Legacy compatibility only**；正式 demo/test path 不得使用，改用 `demo.exportTbFile` → `import.tb.fromFile` |
| `demo.fetchAccountMappingRows` | `{}` | `{ fileName, rows }` | **Legacy compatibility only**；正式 demo/test path 不得使用，改用 `demo.exportAccountMappingFile` → `import.accountMapping.fromFile` |

`DemoProjectDto` 結構（deterministic：同版本程式每次回傳相同內容）：

```json
{
  "project": { "caseName": "範例測試案件", "projectCode": "DEMO-2025-001", "entityName": "...", "operatorId": "...",
               "periodStart": "2025-01-01", "periodEnd": "2025-12-31",
               "lastPeriodStart": "2025-12-31" },
  "gl": { "fileName": "JE-demo-2025.xlsx", "rowCount": 2000,
          "amountMode": "flag", "mapping": { "docNum": "傳票號碼", "...": "..." } },
  "tb": { "fileName": "TB-demo-2025.xlsx", "rowCount": 100,
          "changeMode": "debitCredit", "mapping": { "accNum": "科目代號", "...": "..." } },
  "holidays": ["2025-01-01", "..."],
  "makeupDays": ["2025-02-08"],
  "demoScenario": { "name": "...", "rationale": "...", "groups": ["（filter 條件 AST，見 Filter / Criteria 章節 schema）"] }
}
```

這個測試案件是開發用的 fixture，內含一份近似的 2025 年台灣國定假日清單。它的資料刻意埋進了未來各項規則測試會用到的特徵：帶關鍵字的摘要、落在週末或假日的分錄、整數金額、多位不同的建立人員、以及期末之後才核准的日期。即便如此，每一張傳票本身都是借貸平衡的，TB 的借貸合計也是從 GL 推導出來的；這樣安排是為了讓完整性測試與借貸不平測試兩者都有辦法通過。`demoScenario` 是 MockDataLoader 在進階條件篩選那一步會套用的示範條件 AST。它是 deterministic 的，由後端的 `DemoDataFactory` 提供，前端不會把任何業務資料寫死。這份 AST 可以直接送進 `filter.preview` 或 `filter.commit`。

`AppBootstrapDto` 結構：

```json
{
  "applicationName": "JET",
  "startPage": "https://appassets.example/index.html",
  "supportedActions": ["app.bootstrap", "..."],
  "database": {
    "provider": "Sqlite",
    "isAvailable": true,
    "connectionTarget": "...",
    "mode": "Local"
  },
  "demo": {
    "enabled": true
  }
}
```

### Project Persistence / Host / Dev Actions

本節描述的都是已實作的正式契約。專案採取「每個專案一個資料夾」的方式持久化：每個專案在 `{AppBaseDir}/projects/{projectId}/` 之下有自己的資料夾，裡面放著 `project.json`（存 metadata）與 `jet.db`（一個 SQLite 資料庫）。因為每個專案都是獨立的 DB 檔，SQLite local provider 的資料表就不需要 `project_id` 欄位，因為檔案本身已經界定了 scope。即便如此，repository 介面對外仍然以 `projectId` 為參數，再由它解析出對應的 DB 路徑。

**資料庫 provider 歸屬**：`project.json` 用 `databaseProvider` 欄位記錄這個專案的會計資料實際存放在哪個引擎。目前這個值固定是 `"sqlite"`，也就是本地。未來的雲端專案會是 `"sqlServer"`；屆時所需的連線設定欄位，要先以新的契約欄位寫進這份 manifest，才能動手實作。舊版的 `project.json` 如果缺這個欄位，讀取時一律正規化成 `"sqlite"`，不需要做任何遷移。所有專案組態，包括 field mapping、filter scenario、calendar、rule run 摘要，都持久化在該專案資料庫的 `config_*` 與 `result_*` 資料表以及 `project.json` 裡，因此跨 session 可以復用。除此之外，不存在任何只活在記憶體、或藏在全域旁路的組態儲存。

**儲存 JSON 的可讀性與 SQL Server 可攜性**：`row_json`、`mapping_json`、`columns_json` 以及 `project.json`，一律以未跳脫的 UTF-8 JSON 儲存，做法是用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`。這麼做是為了讓中文原文能直接人工檢視，不會被跳脫成一堆轉義碼。這套儲存形狀也刻意做到對 SQL Server 直接可攜。對應關係有三組：TEXT 對應 `NVARCHAR(MAX)`，可以用 `OPENJSON` 或 `JSON_VALUE` 查詢；scaled INTEGER 對應 `BIGINT`；日期統一存成 "yyyy-MM-dd" 字串，因此不依賴任何 provider 自己的日期函式。投影邏輯落在 `GlRowProjector` 與 `TbRowProjector`，兩者都是與 provider 無關的 C# 純函式。正因如此，要支援 SQL Server 時只需要新增 `SqlServer*` 系列的 repository 實作，把 bulk insert 換成 `SqlBulkCopy` 即可，Application 層完全不用動（見 guide §13）。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `project.create` | `{ caseName?, projectCode, entityName, operatorId, periodStart, periodEnd, lastPeriodStart?, databaseProvider? }` | `{ projectId, ok }` | **Implemented**。建立查核案件並初始化 `{projects}/{projectId}/`（`project.json` + `jet.db`／SQL Server 庫）。`caseName`（選填，案件名稱）提供時作為 `projectId` 與資料夾名，經 `ProjectNameRules` 驗證（允許 Unicode 文字/數字/空白/`_`/`-`/括號；拒絕路徑分隔與保留字元 `/ \ : * ? < > |`、句點、前後空白、Windows 保留名、長度>100）；同名或（SQL Server）淨化後同庫名已存在 → `invalid_payload`。未提供則回退 32-hex GUID（既有程式化/測試建立）；**UI 表單強制必填**。`databaseProvider` 建立時選定（`sqlite`/`sqlServer`），之後不可改 |
| `project.list` | `{}` | `{ projects: [{ projectId, projectCode, entityName, periodStart, periodEnd, createdUtc, currentStep, databaseProvider, lastOpenedUtc\|null }] }` | 列出所有既有專案（依 `lastOpenedUtc ?? createdUtc` 新→舊，最近開啟者浮上）；損壞的 project.json 會被略過。`databaseProvider` 同 provider 歸屬說明；`lastOpenedUtc` = 最近一次 `project.load` 戳記的 UTC 時間（從未開啟過為 null，前端 fallback 顯示 createdUtc） |
| `project.saveProgress` | `{ currentStep }` | `{ ok, currentStep }` | **Implemented**。保存使用者目前所在的流程步驟（0–5 的 6 步索引，對齊 Step Data Outline）到 project.json 的 `currentStep`，供 `project.load` resume「上次操作的位置」。與匯入/配對的自動推進（只前進不後退）不同，本 action 記錄使用者實際所在位置，**允許倒退**。前端於步驟導航時與結束應用程式前呼叫。無 active project → `no_active_project`；`currentStep` 缺漏或超出 0–5 → `invalid_payload` |
| `log.append` | `{ level?, text }` | `{ ok: true }` | **Implemented**。把一則前端「狀態與訊息」持久化到當前專案資料庫的 `app_message_log` 表，每專案保留最近 500 則、舊的自動修剪。`level` 限 `"info"`／`"warn"`：缺省為 info、不分大小寫、落地為小寫，非白名單值 → `invalid_payload`。`text` 必填，trim 後上限 4000 字元、超長截斷。需要 active project（`no_active_project`）——專案選擇畫面尚無 active project，該畫面的訊息只留在前端記憶體、不持久化。這些訊息是 UX 輔助紀錄，不是審計留痕；審計留痕在 `result_*` 表與工作底稿 |
| `log.recent` | `{ limit? }` | `{ messages: [{ occurredUtc, level, text }] }` | **Implemented**。取當前專案最近持久化訊息（新→舊）。`limit` 預設 30、上限 100（超出夾擠）。供 `project.load` 後還原「狀態與訊息」面板的歷史訊息。需要 active project |
| `host.selectFile` | `{ title?, extensions? }` | `{ filePath, fileName }`（使用者取消時兩者皆 `null`，`ok` 仍為 true） | 開啟原生 OpenFileDialog；`extensions` 為副檔名陣列（如 `[".xlsx"]`），host 端組成 Windows filter。host capability 走同一條 action 通道（guide §12 Host） |
| `host.selectFiles` | `{ title?, extensions? }` | `{ files: [{ filePath, fileName }] }`（使用者取消時 `files` 為空陣列，`ok` 仍為 true） | **Implemented**。多選版本的檔案對話框（`OpenFileDialog.Multiselect`），供匯入精靈一次選取多個來源檔。獨立 action 而非 `host.selectFile` 的旗標——避免同一 action 兩種 response 形狀 |
| `host.exitApp` | `{}` | `{ ok: true }` | **Implemented**。請求 host 關閉應用程式視窗（WinForms `Close`，以 `BeginInvoke` 排入訊息佇列）。前端標準結束流程：先 `project.saveProgress` 保存進度，再呼叫本 action；視窗可能在 response 抵達前關閉，前端不得依賴本 action 的 response。純 host capability，不含業務邏輯（guide §12 Host） |
| `host.selectSavePath` | `{ defaultFileName? }` | `{ path }`（使用者取消時 `path` 為 `null`，`ok` 仍為 true） | 開啟原生 SaveFileDialog（filter 固定 `Excel 活頁簿 (*.xlsx)`）取得匯出底稿的存檔路徑；`defaultFileName` 為客戶／公司名片段（前端傳入，省略時 host 以 `WorkingPaper` 為基底），host 端據此組成預填檔名 `{base}_{yyyymmddHHmmss}_WorkingPaper.xlsx`——**時間戳在 host 端產生**（Form1 `DateTime`，非 Domain）。取消回 `path: null`。純 host I/O，不含業務邏輯（guide §12 Host）。供「匯出到其他位置」備用——前端預設匯出已改為**直接落專案目錄**（`export.workpaperStream` 省略 `outputPath`），故此 action 目前非預設流程必經。**Implemented**（E1 Task 6） |
| `host.openFolder` | `{ path }` | `{ ok }` | **Implemented**。在檔案總管揭示一個路徑（開啟所在目錄並選取該檔，`explorer.exe /select`），供匯出底稿後「打開目錄」按鈕用（`path` 為剛匯出的底稿路徑）。`path` 缺/空 → `invalid_payload`。純 host I/O，不含業務邏輯（guide §12 Host） |
| `query.dataPreview` | `{ dataset, limit? }` | `{ dataset, columns, rows, totalCount, stats }` | **Implemented**。**正式版**的使用者資料預覽（細節見下方）；與 dev.db.* 的差異：只開放業務資料集白名單，不暴露任何實體資料表名 |
| `query.completenessDiffPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：完整性全科目差異(diff≠0)keyset 分頁,排序鍵 account_code ASC;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{accountCode,accountName,tbAmount,glAmount,diff,notInTb}` |
| `query.docBalancePage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：借貸不平傳票(SUM(amount_scaled)≠0)keyset 分頁,排序鍵 document_number ASC;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{documentNumber,debit,credit,diff}` |
| `query.nullRecordsPage` | `{ category, cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：空值/期外日期紀錄 keyset 分頁,排序鍵 entry_id ASC;`category` **必填**,白名單四值 `nullAccount`/`nullDocument`/`nullDescription`/`outOfRangeDate`(非法值 `invalid_payload`);`outOfRangeDate` 以**核准日(approval_date／配對 docDate)** 對專案 PeriodStart/End 判定(2026-06-23 決策,對齊舊工具的「Approval date out of period」;非過帳日);cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{documentNumber,accountCode,postDate,description}` |
| `query.filterHitsPage` | `{ scenarioPosition, cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：已存篩選情境(result_filter_run)的命中行層明細 keyset 分頁,排序鍵 entry_id ASC;`scenarioPosition` **必填**(整數;缺則 `invalid_payload`);惰性補算——該 position 在 result_filter_run 無列但 config_filter_scenario 有定義時,先重用 filter.commit 同源 materializer 落地命中再查;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{documentNumber,lineItem,postDate,accountCode,accountName,amount,drCr,description}` |
| `query.infSamplePage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：INF 抽樣(result_inf_sampling_test_sample,最近一次 validate.run)行層明細 keyset 分頁,排序鍵 entry_id ASC;借/貸由 debit_amount_scaled/credit_amount_scaled 拆欄並換算顯示值;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{documentNumber,accountCode,accountName,debit,credit,postDate,approvalDate,createdBy,approvedBy,description}` |
| `query.tagMatrixScenarios` | `{}` | `{ scenarios: [{ position, name, voucherHitCount, rowHitCount }] }` | **Implemented (D2)**：多情境 tag 矩陣的情境摘要;由 `result_filter_run` 即時算出每個已存情境(位置 1..N)的傳票層命中數(`COUNT(DISTINCT document_number)`)與行層命中數(`COUNT(*)`)。`name` 取自 config_filter_scenario,依 position 升冪;無命中的情境列出 count=0。惰性補算同 filterHitsPage(全空且有情境 → 落地後重取)。需要 active project |
| `query.tagMatrixVoucherPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented (D2)**：tag 矩陣的傳票層 keyset 分頁,排序鍵 document_number ASC(排除 NULL 傳票號);由 `result_filter_run` 即時 pivot,採每頁兩段查詢(命中傳票 keyset 頁 + 同鍵範圍命中位置)。rows 每列 `{documentNumber,postDate,createdBy,voucherTotal,matchedPositions}`,`voucherTotal` 為該傳票借方總額顯示值(`SUM(debit_amount_scaled)` 換算),`matchedPositions` 為命中的情境位置陣列(1..N,有序去重);cursor opaque、pageSize 預設 200/上限 500;惰性補算同 filterHitsPage |
| `query.tagMatrixRowPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented (D2)**：tag 矩陣的行層 keyset 分頁(命中傳票之所有行,含該傳票內未命中任何情境的行),排序鍵 entry_id ASC(排除 NULL 傳票號);由 `result_filter_run` 即時 pivot,採每頁兩段查詢(命中傳票之所有行 keyset 頁 + 同鍵範圍各行命中位置)。rows 每列 `{documentNumber,lineItem,postDate,approvalDate,createdBy,approvedBy,accountCode,accountName,amount,matchedPositions,description}`,`amount` 為該行 signed 金額顯示值(`amount_scaled` 換算),`matchedPositions` 為該行命中的情境位置陣列(有序去重;**非命中行為空 `[]`**);cursor opaque、pageSize 預設 200/上限 500;惰性補算同 filterHitsPage |

> `query.tagMatrix*` 這三個 action 屬於子專案 D2（多情境 tag 矩陣），形狀已經鎖定。`tagMatrixScenarios`、`tagMatrixVoucherPage`、`tagMatrixRowPage` 都已實作。這個矩陣不會落地成新的資料表，而是由 `result_filter_run`（也就是 D1 階段落地的命中結果）即時算出來，算的是方法學的 step4（傳票層的 C1..CN 布林）與 step4-1（行層逐行的 tag）。其中 `matchedPositions` 就是命中的情境位置集合，對映到 C1..CN。cursor 是 opaque 的，壞掉的 cursor 回 `invalid_payload`（與下方的 cursor 契約相同）；pageSize 預設 200、上限 500。

上面這些 `query.*Page` 共用一套 **cursor 契約**：`cursor` 省略、null 或空字串都代表取首頁（不帶游標述詞）。但如果有傳 cursor、卻無法解碼（不是 opaque 格式），就回 `invalid_payload`。這裡 handler 刻意 fail loud，不會默默把它重置為首頁；這是為了貫徹「游標格式不符就讓 handler 報參數錯、不靜默吞掉」的原則。

| `dev.db.overview` | `{}` | `{ databasePath, databaseProvider, fileSizeBytes, sqliteVersion, tables: [{ name, rowCount }] }` | **Dev-only 診斷**：當前專案資料庫總覽。**僅 Debug 組建註冊**——Release 組建不註冊此 action（呼叫會得到 `bridge_error` unknown action），前端也依 `system.ping.devToolsEnabled` 隱藏開發面板。走**獨立唯讀路徑**：連線指定 `Mode=ReadOnly`、不共用快取、不開連線池，直接讀磁碟上的 DB 檔。因此檢視**零副作用**——不建 schema、不寫入，看到的必然是已持久化的資料，而非記憶體狀態。DB 檔不存在 → `file_not_found`（不會建立）。這不是審計 workflow 的一部分，UI 置於折疊的開發面板 |
| `dev.db.tableData` | `{ tableName, limit?, offset? }` | `{ tableName, columns, rows, totalCount, limit, offset }` | **Dev-only 診斷**：分頁讀資料表，同上唯讀語意與**僅 Debug 組建註冊**。`limit` 預設 50（上限 200）；`tableName` 以 sqlite_master 白名單精確比對，否則 `table_not_allowed`。`rows` 為字串化 cell 陣列，SQL NULL → JSON null。dev 工具允許 OFFSET 分頁（正式 GL 分頁仍須 keyset） |
| `dev.log.export` | `{}` | `{ ndjson }` | **Dev-only 診斷**：把診斷日誌（第三層、跨專案）的 ring buffer 完整匯出為 NDJSON，每行是一筆完整 JSON 物件:`timestamp`/`level`/`category`/`eventName`/`message`/`correlationId`/`transactionId`/`projectId`/`fields`/`exception`。記錄的內容包含 action 生命週期、SQL（完整命令加上參數 name=value、`rows_affected`、`provider`）、transaction（begin/commit/rollback 共享同一個 `transaction_id`）、exception（含 inner）與大檔 milestone。這層獨立於 result_*（審計）與 `IMessageLogStore`（UX 訊息）兩層。**僅 Debug 組建註冊**——Release 不註冊此 action（`bridge_error` unknown action），也不註冊 `RingBufferLoggerProvider`（log 變 no-op），前端則依 `system.ping.devToolsEnabled` 隱藏「DEV — 診斷日誌匯出」面板。不需 active project（跨專案）。前端以唯讀 textarea 呈現可複製的 NDJSON |

`query.dataPreview` 細節（正式版的使用者資料預覽）：

- **用途**：讓使用者直觀看到自己目前操作的資料長什麼樣子。具體有兩個場景：欄位配對時對照欄名與實際內容是否吻合，以及進階篩選之前先掌握數值、日期、摘要的大概樣貌。要注意這是「有界預覽」，不是分頁瀏覽。完整的明細分頁屬於 `query.*Page` 那個里程碑（採 keyset 分頁），這個 action 絕對不會回傳完整母體。
- `dataset` 白名單列出可預覽的業務資料集。每個資料集的 wire key 是小駝峰命名；對外顯示時則用 `JetSchemaCatalog` 裡登記的正準審計名（canonical audit name）。catalog 是命名的單一事實來源，前端 picker 上的標籤只是鏡射 catalog，並非另一套命名。各資料集如下：
  - `"glStaging"`（**JE_PBC**）與 `"tbStaging"`（**TB_PBC**）：匯入後的來源原貌。它的 columns 是正規化後的來源欄名，與欄位配對下拉選單裡看到的一字不差；每個 cell 都是未經處理的原始字串。
  - `"glEntries"`（**JE**）：投影後的 GL 分錄，也就是測試母體。columns 固定為 `documentNumber, lineItem, postDate, accountCode, accountName, documentDescription, amount, drCr`，這組欄位與 `filter.preview` 回傳的 previewRows 相同。其中 `amount` 是帶正負號的顯示值。
  - `"tbBalances"`（**TB**）：投影後的 TB 餘額。columns 固定為 `accountCode, accountName, changeAmount`。
  - `"accountMappings"`（**ACCOUNT_MAPPING**）：已匯入的科目配對表。columns 固定為 `accountCode, accountName, standardizedCategory`。
  - `"authorizedPreparers"`（**AUTHORIZED_PREPARER**）：已匯入的授權編製人員清單。columns 固定為 `preparerName`。
  - `"dateDimension"`（**DATE_DIMENSION**）：已匯入的事務所假日與補班日，資料來源是 `staging_calendar_raw_day`。columns 固定為 `date, dayType, dayName`。其中 `dayType` 是 `holiday` 或 `makeup` 的原值；`dayName` 是假日名稱或補班說明，缺漏時為 null。各列依 `date` 升冪排序。這個資料集沒有 `stats`。
  - `"schemaOverview"`（**資料庫結構總覽**）：列出這個專案的資料表結構。它和其他資料集最大的不同在於：它的 rows 來自 `JetSchemaCatalog` 的 metadata，而不是任何一張資料表的實際列資料。它會列出 audience 標為 `DataView` 或 `StructureOnly` 的條目，標為 `Hidden` 的不列；每一列對應 catalog 的一筆登錄。columns 固定為 `canonicalName, physicalName, layer, audience, browsable`。其中 `layer` 是審計層，取值為 `Source`、`Staging`、`Target` 或 `System`；`audience` 是曝光程度，取值為 `DataView` 或 `StructureOnly`；`browsable` 在 audience 等於 `DataView` 時為 `"是"`，否則為 `"—"`。各列順序依 catalog 的宣告順序。`totalCount` 等於列出的條目數。這個資料集不依賴任何實際資料，所以永遠有列可顯示，這點與專案是否有資料無關；不過呼叫它仍然需要有 active project。它沒有 `stats`。
  - 傳入不在白名單內的值，回 `invalid_payload`。
- `limit`：預設 50、上限 100，超出上限會夾擠回 100。rows 依匯入或投影的順序取前 N 列，cell 一律是字串，SQL 的 NULL 對應成 JSON 的 null。`schemaOverview` 例外：它的列數恆等於 catalog 曝光條目數（遠少於 50），所以 limit 對它沒有作用。
- `totalCount` 是該資料集目前的總列數。如果資料集還沒有任何資料（尚未匯入或尚未投影），回傳的是 `{ columns: [], rows: [], totalCount: 0, stats: null }`。這不是錯誤，而是讓前端顯示空狀態。`schemaOverview` 由 catalog 驅動，因此永遠有列、不會走到空狀態。
- 只有 `glEntries` 這個資料集會附帶 `stats`，它是進階篩選用來把關的資訊：`{ amountAbsMin, amountAbsMax, postDateMin, postDateMax, voucherCount }`。其中金額是 `ABS(amount_scaled)` 換算後的顯示值，之所以取絕對值，是因為篩選的數值區間比較的就是絕對值；日期是 ISO 字串；`voucherCount` 是不重複的傳票數。其餘資料集的 `stats` 一律為 null。
- 這個 action 需要 active project，否則回 `no_active_project`。權威的計算仍然落在 SQL，用的是 set-based 的 COUNT、MIN、MAX；這個 action 本身只是唯讀預覽，與規則執行無關。`dateDimension` 與 `schemaOverview` 屬於正準目錄檢視，但讀取來源不同：`dateDimension` 讀 `staging_calendar_raw_day` 這張資料表，`schemaOverview` 讀 Domain 層的 `JetSchemaCatalog`，完全不查資料庫。

### Project / Import

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `project.create` | `{ projectCode, entityName, operatorId, periodStart, periodEnd, lastPeriodStart?, databaseProvider? }` | `{ projectId, ok }` | 建立專案：建立 `projects/{projectId}/` 資料夾、寫入 `project.json`、初始化專案資料庫 schema、設定 current session。日期格式 `yyyy-MM-dd`；`lastPeriodStart` 存為 `lastAccountingPeriodDate`；`moneyScale` 預設 10000、`roundingMode` 預設 `AwayFromZero`。`databaseProvider` 選定資料引擎，**只在建立時可選、之後不可改**（guide §13）：省略或 `"sqlite"` → 每專案一個本機 `jet.db`；`"sqlServer"` → 在共用 instance 上每專案一個 `JET_{projectId}` 資料庫（連線取自環境變數 `JET_SQLSERVER_CONNECTION`，相容 Express／Standard／Enterprise）。其他值 → `invalid_payload`。選定結果記錄於 project.json |
| `project.load` | `{ projectId }` | `{ project, mapping: { gl\|null, tb\|null }, importState: { gl\|null, tb\|null, accountMapping\|null, authorizedPreparer\|null, calendar }, latestRuns: { validate\|null, prescreen\|null }, filterScenarios: [] }` | 載入既有專案並設定 session；回傳完整 resume 狀態供重啟後接續。`mapping.gl = { mapping, amountMode, sourceBatchId, committedUtc }`；`importState.gl = { batchId, rowCount, columns, fileName, importedUtc, sources }`（`sources` 形狀同 `import.*.fromFile` response；`fileName` = 第一個來源檔名，向後相容）；`importState.accountMapping = { batchId, rowCount, fileName, importedUtc }`（科目配對未匯入時 null）；`importState.authorizedPreparer = { rowCount }`（授權編製人員清單未匯入時 null；清單是 name 集合、不入 import_batch，故 resume 只回 rowCount，無 fileName/importedUtc）；`importState.calendar = { holidayCount, makeupDayCount, nonWorkingDays }`（`nonWorkingDays` = 非工作日週幾集合，.NET DayOfWeek 編碼週日=0…週六=6；未設定回預設 `[0,6]`=週六日）；mapping 是否已 commit 由 `mapping.gl !== null` 推導。`latestRuns.validate` / `latestRuns.prescreen` = 最近一次 `validate.run` / `prescreen.run` 的完整 response（自 `result_rule_run.summary_json` 原樣回放；schema v3 遷移後為 null，重跑即恢復）。**結果失效不變量**：規則結果是衍生資料。任何改寫其上游的操作（GL/TB re-import 或 re-commit mapping、科目配對匯入、行事曆匯入）都會在同一交易內清除 `result_rule_run` 與抽樣表。因此上游一變動，`latestRuns.*` 即回 null，前端顯示「未執行」並要求重跑——不會回放對應到已不存在資料的舊結果。`filterScenarios = [{ name, rationale, groups, savedUtc }]`（自 `config_filter_scenario`，依 position 排序；v2 時代保存的 `prescreenKey` 已由遷移翻譯為新鍵）；`project.databaseProvider` 標示資料引擎（見上方 provider 歸屬說明）。**載入時戳記 `lastOpenedUtc`**（回寫 project.json，供 `project.list` 依最近開啟排序與顯示） |
| `project.delete` | `{ projectId }` | `{ ok, projectId }` | 永久刪除專案：先刪除該專案資料庫（SQLite 刪 `jet.db`／SQL Server `DROP DATABASE JET_{projectId}`，先 `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` 斷線），再刪除 `projects/{projectId}/` 資料夾。**硬刪、不可復原**（無 soft delete/還原）；**不需 active project**（從專案選擇畫面呼叫）。`projectId` 不存在 → `project_not_found`；選 sqlServer 但連線未設定（`JET_SQLSERVER_CONNECTION` 缺）→ `sql_server_not_configured`（此時資料夾保留、不刪，供修正連線後重試）。若刪除的是當前 session 專案則一併清空 session |
| `import.gl.fromFile` | `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | **Scale-aware**：從 `.xlsx` / `.csv` / `.txt` 檔案路徑串流讀 GL 寫入 `staging_gl_raw_row`；payload 不帶 rows。`mode:"append"` 支援多來源合併（見下方細節） |
| `import.gl` | `{ fileName, rows, columns }` | `{ fileName, rows, columns }` | **Legacy compatibility only**；正式 UI/demo/import path 不得使用，新代碼請改用 `import.gl.fromFile` |
| `import.tb.fromFile` | `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | **Scale-aware**：從 `.xlsx` / `.csv` / `.txt` 檔案路徑串流讀 TB 寫入 `staging_tb_raw_row`；payload 不帶 rows。語意對齊 `import.gl.fromFile` |
| `import.tb` | `{ fileName, rows, columns }` | `{ fileName, rows, columns }` | **Legacy compatibility only**；正式 UI/demo/import path 不得使用，新代碼請改用 `import.tb.fromFile` |
| `import.accountMapping.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, columns, fileName, importedUtc }` | **Implemented／Scale-aware**：從 `.xlsx` / `.csv` 檔案路徑串流讀科目配對表寫入 `staging_account_mapping_raw_row` 並投影 `target_account_mapping`；payload 不帶 rows。細節見下方 |
| `import.accountMapping` | `{ fileName, rows }` | `{ fileName, rows }` | **Legacy compatibility only**；正式 UI/demo/import path 不得使用，新代碼請改用 `import.accountMapping.fromFile` |
| `import.authorizedPreparer.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, fileName, importedUtc }` | **Implemented**：從**單欄** `.xlsx` 讀授權編製人員清單寫入 `staging_authorized_preparer_raw_row` 並投影 `target_authorized_preparer`（name PK）；payload 不帶 rows。細節見下方 |
| `import.inspectFile` | `{ filePath }` | `{ fileType, worksheets, columns, encoding, delimiter }` | **Implemented**。匯入前的唯讀檔案檢視（精靈預覽用），細節見下方 |
| `import.previewFile` | `{ filePath, sheetName?, encoding?, delimiter?, limit? }` | `{ columns, sampleRows }` | **Implemented**。匯入前的逐來源有界預覽（讀標頭 + 前 N 列原貌），細節見下方 |
| `import.holiday` | `{ dates: ["yyyy-MM-dd", …] }` | `{ count }` | **Implemented**。寫入 `staging_calendar_raw_day`（day_type=`holiday`，replace 語意：同 type 先清後寫）。日期格式錯誤 → `invalid_payload`。假日名稱欄位列為未來擴充（假日過帳/核准底稿需要時先修本 manifest） |
| `import.makeupDay` | `{ dates: ["yyyy-MM-dd", …] }` | `{ count }` | **Implemented**。同上，day_type=`makeup` |
| `import.holiday.fromFile` | `{ filePath, fileName?, sheetName? }` | `{ count }` | **Implemented**。從 `.xlsx` 讀事務所假日表（第 1 列樣式標題、第 2 列標頭 `Date_of_Holiday/Holiday_Name/IS_Holiday`，只收 IS_Holiday=Y、缺該欄則全收）寫入 `staging_calendar_raw_day`（`day_type='holiday'`，replace、含 `day_name`）；同交易清結果。僅 `.xlsx`。錯誤碼見下方 |
| `import.makeupDay.fromFile` | `{ filePath, fileName?, sheetName? }` | `{ count }` | **Implemented**。同上，工作表/欄 `Date_of_MakeUpday/MakeUpDay_Desc`，`day_type='makeup'`（無 IS_Holiday 過濾） |
| `calendar.setNonWorkingDays` | `{ days: [int, …] }` | `{ ok, nonWorkingDays }` | **Implemented**。設定每案「非工作日是週幾」（.NET DayOfWeek 編碼，週日=0…週六=6），寫入 `project.json` 的 `nonWorkingDays`。未設定時預設週六、週日（canonical `[0,6]`），完全重現舊有週末判定；影響週末過帳/核准規則與週末篩選條件。空集合 = 整週皆工作日。值不在 0–6 或非整數陣列 → `invalid_payload`；需要 active project（`no_active_project`） |

`import.gl.fromFile` 細節：

- `filePath`：本機的絕對路徑。支援 `.xlsx`、`.csv`、`.txt` 三種，其中 `.txt` 的內容當作 CSV 處理。其他副檔名會回 `unsupported_file_type`。
- `fileName`：選填。省略時預設從 `filePath` 取檔名。
- `mode`：取 `"replace"`（預設）或 `"append"`，其他值回 `unsupported_mode`。兩種模式的行為如下：
  - `replace` 會在同一個 transaction 內，先清掉該 dataset 的舊批次、staging rows，以及 target rows 與已 commit 的 mapping；之所以連 mapping 一起清，是因為重新匯入會讓原本的配對失效，前端必須重新 commit。清完之後，再用這次的來源開一個新批次，這次來源就是新批次的第一個來源。
  - `append` 則是把這次的來源加進該 dataset 現有的批次。這是多來源合併的機制：一個 GL 或 TB 資料集對應一個批次，而這個批次可以由多個檔案或多個工作表組成。它的語意有幾條：
    - 如果該 dataset 還沒有任何批次，回 `no_import_batch`，因為第一個來源必須走 replace 才能開批次。
    - 這次來源的有效欄名集合必須與既有批次一致。比對與順序無關，欄序以批次的第一個來源為準。若不一致，回 `column_mismatch`，訊息會列出雙向差集，也就是「來源多出的欄」與「來源缺少的欄」。驗證分兩個階段：串流之前先比對具名標頭的集合，好讓不符的情況快速失敗；串流完成之後，再用收斂後的有效欄位集合（定義見下方）做最終檢查。任一階段不符，就 rollback 這次來源，既有批次不受影響。
    - 附加成功與下游失效落在同一個 transaction 裡，一起發生：寫入來源紀錄與 staging rows、把批次的 `rowCount` 累加上去，同時清掉該 dataset 的 target rows 與已 commit 的 mapping。這點與 replace 相同，因此前端事後同樣要重新 commit 配對。
    - 如果這次來源有 0 筆資料列，rollback 並回 `empty_workbook`，既有批次不受影響。
    - 對於多工作表的 `.xlsx`，做法是逐工作表呼叫這個 action：第一個工作表走 replace，其後的工作表走 append，每次都帶上對應的 `sheetName`。
- Response 的 `rowCount` 是批次的總列數；`addedRowCount` 是這一次寫入的列數。走 replace 時，因為批次只有這一個來源，兩者會相等。
- Response 的 `sources` 是批次的來源清單，依匯入順序排列：`[{ sourceNo, fileName, sheetName|null, encoding|null, delimiter|null, rowCount, importedUtc }]`。其中 `sheetName`、`encoding`、`delimiter` 記錄的是呼叫當下指定的值；若為 null，表示這個值是交由偵測鏈自動判定的。
- Response 的 `columns` 是批次的有效欄位集合，收斂規則見 guide §3.1.5。規則是：具名標頭一律保留；至於空白標頭那種 `COL_{n}` 佔位欄，只有在該欄實際出現過至少一個非空值時才保留。因此同一個工作表，`import.inspectFile` 回的 `columns`（標頭列原貌，含佔位欄）可能比匯入後批次的 `columns` 多。這是正確行為，不是資料遺失，因為被剔除的佔位欄整欄根本沒有資料。
- 匯入串流的過程中，每讀滿 20,000 列就推播一次 `import.progress` 事件，細節見「Host→Web 事件」章節。

`import.inspectFile` 細節：

- 這個 action 是唯讀的、零副作用，而且不需要 active project，所以在建立案件之前就能預覽；它不回傳任何資料列。它的用途是讓匯入精靈在真正匯入之前先看看檔案結構，作為人工把關點，及早攔下編碼或分隔符判錯的情況。檢視只讀到標頭列就停下（streaming early-exit），因此檔案多大都不影響回應時間。
- 檔案是 `.xlsx` 時，回 `{ fileType: "xlsx", worksheets: [{ name, columns, rowCountEstimate }], columns: null, encoding: null, delimiter: null }`，列出全部工作表與各自正規化後的欄名（空工作表的 `columns` 是空陣列）。`rowCountEstimate` 是推估的資料列數（nullable int），算法是取工作表 `<dimension>` 元素的末列號減去標頭列號；當 dimension 缺席或無法解析時為 null。這個 dimension 是由產生檔案的軟體維護的，可能已經過時，所以這個欄位只能用來在精靈裡顯示規模預期與進度估算，不得拿去做任何驗證或匯入判斷。實際列數一律以匯入 response 的 `rowCount` 與 `addedRowCount` 為準。
- 要注意 worksheets 的 `columns` 反映的是標頭列原貌，空白標頭會以 `COL_{n}` 佔位呈現。匯入之後批次的 `columns` 則是收斂後的有效集合，沒有資料的佔位欄已被剔除（理由見 `import.gl.fromFile` 細節）。兩者欄數可能不同，這是正確行為。
- 檔案是 `.csv` 或 `.txt` 時，回 `{ fileType: "csv", worksheets: null, columns: [...], encoding, delimiter }`。其中 `encoding` 與 `delimiter` 是偵測鏈判定出來的結果（例如 `"big5"`、`","`；單欄檔的 `delimiter` 為 null），可以直接拿去當作 `import.*.fromFile` 的覆寫參數。
- 錯誤碼與匯入相同：`file_not_found`、`unsupported_file_type`、`file_read_error`、以及 CSV 無標頭時的 `empty_workbook`。
- `sheetName`：選填，只對 `.xlsx` 有效。缺省時指第一個工作表。指定的工作表不存在時回 `sheet_not_found`；對 `.csv` 或 `.txt` 提供這個參數則回 `invalid_payload`。
- `encoding`：選填，只對 `.csv` 與 `.txt` 有效。白名單是 `"utf-8"`、`"big5"`、`"utf-16"`（不分大小寫）。缺省時走偵測鏈，順序是先看 BOM、再做嚴格的 UTF-8 驗證、最後落到 Big5（見 guide §3.1.1）。傳入非白名單值、或對 `.xlsx` 提供這個參數，回 `invalid_payload`。
- `delimiter`：選填，只對 `.csv` 與 `.txt` 有效。白名單是 `","`、`"\t"`、`";"`、`"|"`（皆為單字元字串）。缺省時走引號感知的取樣統計偵測（見 guide §3.1.1）。傳入非白名單值、或對 `.xlsx` 提供這個參數，回 `invalid_payload`。
- Response 的 `columns` 是正規化之後的標頭列：經過 trim、空白標頭命名為 `COL_{n}`、重複的標頭加上 `_2` 或 `_3` 字尾。這組欄名供 `mapping.autoSuggest` 與 `mapping.commit.gl` 使用；staging `row_json` 的 key 也用同一套名稱。
- 規模約束（scale constraint）：response 絕對不回 rows，明細只能透過後續的 paging query 取得。
- 正式資料、demo 與測試 pipeline、以及任何可能進入 scale path 的匯入，都必須走這個 action；`import.gl` 只保留作 legacy fallback。

`import.previewFile` 細節：

- **用途**：讓匯入精靈在使用者按下〔開始匯入〕之前，逐個來源預覽「正規化後的標頭，加上前 N 列的原貌」。這是用來判讀「這份檔案到底有沒有標頭列」的人工把關點，因為 PBC 的原始檔常常整份都是資料、根本沒有標頭列。
- 這個 action 是唯讀的、零副作用、不需要 active project。預覽是有界的：只讀標頭再加上最多 `limit` 列就 early-exit，絕對不會回傳完整母體。讀檔、編碼偵測與標頭正規化，都沿用 `import.inspectFile` 與正式匯入的同一條處理鏈。
- `columns`：正規化後的標頭列，經過 trim、空白標頭命名為 `COL_{n}`、重複標頭加 `_2` 或 `_3` 字尾。這組欄名與 `import.inspectFile` 以及欄位配對下拉選單裡看到的一字不差。
- `sampleRows`：資料列的陣列，至多 `limit` 列。每一列是對齊 `columns` 的字串 cell 陣列，空 cell 對應成 JSON 的 null。讀取順序就是來源檔內的順序，全空的列會略過，這點與匯入一致。
- `sampleRows` 的 cell 一律對齊 `columns`（也就是標頭欄）。如果某個資料列有超出標頭範圍的儲存格（也就是 ragged 列），預覽不會把這些多出來的儲存格呈現出來。這是刻意的，因為預覽的目的是判讀標頭，不是還原檔案的完整原貌。
- `limit`：預設 10、上限也是 10，超出會夾擠回 10。
- `sheetName`、`encoding`、`delimiter` 的適用條件與白名單，都和 `import.gl.fromFile` 相同：`sheetName` 只能用於 `.xlsx`，`encoding` 與 `delimiter` 只能用於 `.csv` 與 `.txt`，違反就回 `invalid_payload`。這裡之所以開放這些覆寫參數，是因為使用者可能在精靈裡改了 CSV 的編碼或分隔符，然後重新展開預覽，這時預覽內容就應該隨之改變。
- 錯誤碼比照 inspect 與匯入：`file_not_found`、`unsupported_file_type`、`sheet_not_found`、`invalid_payload`、`file_read_error`、以及 CSV 無標頭時的 `empty_workbook`。

`import.accountMapping.fromFile` 細節：

- 科目配對表的格式固定為三欄（見 guide §2.3）：科目代號、科目名稱、標準化分類。欄位的辨識方式是：先用關鍵字去命中正規化後的標頭（「科目代號／account code」對第一欄、「科目名稱／account name」對第二欄、「分類／category」對第三欄），命不中時才退回依位次 1、2、3 對應。事務所底稿格式常用的英文標頭 `GL_NUMBER`、`GL_NAME`、`STANDARDIZED_ACCOUNT_NAME` 也已被關鍵字涵蓋；位次 1、2、3 的 fallback 仍然保留。科目配對表不經過欄位配對那一步，因為它的格式是固定的，所以匯入時直接投影：staging 寫入與 `target_account_mapping` 的投影落在同一個 transaction。
- 標準化分類的白名單是 `Revenue`、`Receivables`、`Cash`、`Receipt in advance`、`Others`。比對時先 trim、再不分大小寫，落地時統一寫成上述的正準大小寫。分類值不在白名單內時回 `projection_failed`，訊息會含列號與原值（最多前 10 筆），整批 rollback。
- 支援 `.xlsx` 與 `.csv`，不支援 `.txt`；其他副檔名回 `unsupported_file_type`。
- `mode`：只接受 `"replace"`（預設）。傳 `"append"` 或其他值回 `unsupported_mode`，因為科目配對表是一份整份替換的設定檔，不做多來源合併。replace 會在同一個 transaction 內，先清掉舊批次與 staging、target rows，再重建。
- 同一個科目代號如果重複出現，後出現的列覆蓋先出現的列。這是投影層的 last-wins 去重，目的是避免同一科目同時落入兩種分類，否則借貸組合的判定會出現歧義。來源若有 0 筆資料列，回 `empty_workbook`。
- 匯入成功之後，會解鎖「未預期借貸組合」這項預篩選，以及 `accountPair` 這個篩選條件。重新匯入不影響既有的 GL、TB 批次與配對。

`import.authorizedPreparer.fromFile` 細節：

- 這是一份單欄的姓名清單。欄位辨識先用關鍵字去命中正規化後的標頭（`AUTHORIZED_PREPARER`、`preparer`、`編製人員`、`姓名`、`name`），命不中時退回位次 1。至少要有一欄，否則回 `projection_failed`。
- 只支援 `.xlsx`，也就是事務所的授權清單範本；其他副檔名（包含 `.csv`）回 `unsupported_file_type`。
- 姓名一律先做 TRIM 正規化，空白列略過，並做去重，因為 name 是主鍵（PK），語意上是個集合。`rowCount` 是去重之後實際落地的筆數。
- `mode`：只接受 `"replace"`（預設）。傳 `"append"` 或其他值回 `unsupported_mode`，因為授權清單是一份整份替換的設定檔，不做多來源合併。replace 會在同一個 transaction 內，先清掉舊的 staging、target rows，再重建。
- 這個 action 不寫 `import_batch` 與 `import_batch_source`，因為授權清單不納入 dataset_kind 那一套體系。response 裡的 `batchId` 只是這一次回應用的識別碼，並不持久化。
- 匯入成功之後會解鎖「非授權編製人員」這項預篩選（C5）。重新匯入時，會在同一個 transaction 內呼叫 `RuleRunResultReset`，讓依賴這份清單的規則結果失效。

`import.holiday.fromFile` / `import.makeupDay.fromFile` 細節：

- 只支援 `.xlsx`，因為事務所範本帶有樣式化的標題列；非 `.xlsx` 回 `unsupported_file_type`。標頭固定在第 2 列，第 1 列是樣式標題，後端用 reader 的 `LeadingRowsToSkip=1` 把它略過。
- 欄位辨識：日期欄以標準名的關鍵字命中（假日是 `Date_of_Holiday`、補班是 `Date_of_MakeUpday`），這一欄是必有的，缺了就回 `projection_failed`，訊息會點名缺哪一欄。名稱欄（`Holiday_Name` 或 `MakeUpDay_Desc`）與 `IS_Holiday` 欄則是選用的。
- 假日有一道過濾：當 `IS_Holiday` 欄存在時，只收值為 `Y` 的列（trim 後不分大小寫），值為 `N` 或空白的略過；若這一欄缺席，則全部收下。補班沒有這道過濾。
- 多年度的資料照單全收，不依檔名上的年度做過濾；同一天會去重，先讀到的那筆勝出。
- 只要有任何一列的日期不是 `yyyy-MM-dd` 格式，就回 `projection_failed`，訊息含列號與原值（最多前 10 筆），且整批不寫入。如果標頭存在但底下 0 筆資料列，回 `count=0`，這等於用一份空清單做 replace，也就是把該 type 清空。
- 語意是 replace，並在同一個 transaction 內清掉規則結果（`RuleRunResultReset`）。至於舊的、payload 帶 `dates` 陣列的 action，仍保留作為相容與 demo 路徑（這條路徑沒有名稱，名稱為 null）。

### Mapping

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `mapping.autoSuggest` | `{ fields, columns }` | `{ suggested }` | 依欄位標籤與關鍵字自動配對（後端執行；只回命中的 key） |
| `mapping.commit.gl` | `{ mapping, amountMode }` | `{ ok, mapping, amountMode, batchId, projectedRowCount, warnings }` | 提交 GL logical mapping 並將最新 GL staging 批次投影到 `target_gl_entry`。`amountMode` 必填：`signed`（單一帶號金額欄）\| `side`（金額+借貸別文字欄）\| `flag`（金額+借方旗標欄）\| `dual`（借方欄−貸方欄），對應 guide §2.1 四模式。`warnings` 為非阻斷提醒字串陣列（可空）：投影成功但必填文字欄（傳票號碼／會計科目編號／會計科目名稱／傳票摘要）整欄空白時，逐欄列出疑似配錯的來源欄（如來源重複標頭中的空白欄），供前端提交成功後就近提示；不影響投影結果 |
| `mapping.commit.tb` | `{ mapping, changeMode }` | `{ ok, mapping, changeMode, batchId, projectedRowCount }` | 提交 TB logical mapping 並投影到 `target_tb_balance`。`changeMode` 必填：`direct`（直接變動金額）\| `debitCredit`（借方−貸方） |

投影的語意是「匯入階段的正規化」（import-stage normalization）。後端以 streaming 方式逐列讀 staging，把金額用 `decimal` 解析後乘上專案的 `MoneyScale`，轉成 scaled integer（見 guide §1.5.3）。轉換出錯時會逐列回報，訊息含 Excel 列號、欄名與原值；只要有任何一列失敗，整批就 rollback 並回 `projection_failed`。除了列級錯誤，還有一道針對退化母體的守門：如果投影沒有任何列級錯誤、母體也不是空的，但整個 GL 母體的借貸總額竟然全部是 0，這通常表示金額欄被誤配到了「傳票總額」欄或空欄，這樣的資料無法用於完整性測試與後續規則，因此整批 rollback 並回 `gl_amounts_all_zero`。順帶一提，guide §1.5.2 講的 set-based pushdown 約束的是驗證（V）、預篩選（R）、篩選（Filter）三類規則的計算，不受這道守門影響。

**傳票文件項次（`line_item`）的自動編號**：`mapping.commit.gl` 提交時，分兩種情況。如果 `lineID` 沒有對應到任何來源欄，投影落地之後會在同一個 transaction 內，用 `ROW_NUMBER() OVER (PARTITION BY document_number ORDER BY source_row_number)` 替每張傳票自動補上 `line_item`。如果 `lineID` 有對應，則照來源逐字寫入、不自動編號。這個 `line_item` 只是文字形式的衍生顯示值，它不參與任何驗證、預篩選、篩選的計算，也不作為任何規則或抽樣的鍵（抽樣依的是 `source_row_number`），因此它的存在不會改變任何測試結果。wire shape 維持不變，SQLite 與 SQL Server 兩個 provider 行為等價。

`dcDebitCode` 這個 mapping 值比較特別：它是借方代碼的字面值（例如 `"D"`、`"1"`），而不是來源欄位的名稱；比對方式是先 trim 再做不分大小寫的文字相等。除了它以外，其餘的 mapping 值都必須是匯入批次 `columns` 裡確實存在的欄位名稱，否則回 `mapping_column_not_found`。

`fields` 通常來自 UI 的欄位定義陣列。每個元素長這樣：

```json
{
  "key": "docNum",
  "label": "傳票號碼",
  "req": true,
  "type": "mix"
}
```

### Validation

規則命名一律使用具體名稱，不再用 V1–V4 這類代號。具體名稱在不同層有不同寫法：wire key 用 lowerCamelCase、資料表 slug 用 snake_case、UI 顯示用中文名。三者的正準對照見 guide §4 的命名登錄表。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `validate.run` | `{}` | 見下方 response 形狀 | **Implemented**。以 SQL set-based 對 `target_gl_entry` / `target_tb_balance` 執行四項資料驗證 summary；完整 response 以 `JetJsonStorage` 存入 `result_rule_run`（resume 用），INF 抽樣樣本落地 `result_inf_sampling_test_sample`（guide §4：抽樣必須可重現） |
| `query.validationDetailsPage` | `{ projectId, kind, cursor?, pageSize? }` | `{ rows, nextCursor }` | Planned（明細分頁屬匯出/明細里程碑；本版 UI 消費 summary counts 與 `completenessTest.diffAccounts`／`docBalanceTest.unbalancedDocuments`／`nullRecordsTest.nullRows` 有界清單） |

`validate.run` response（金額欄位為 scaled ÷ MoneyScale 的顯示值；權威計算只在 SQL 的 scaled BIGINT）：

```json
{
  "stats": { "glRowCount": 2000, "voucherCount": 491, "totalDebit": 0.0, "totalCredit": 0.0,
             "net": 0.0, "periodStart": "2025-01-01", "periodEnd": "2025-12-31" },
  "completenessTest": { "status": "V", "naReason": null, "diffAccountCount": 0,
          "diffAccounts": [ { "accountCode": "", "accountName": "", "tbAmount": 0.0, "glAmount": 0.0, "diff": 0.0, "notInTb": false } ],
          "partA": { "sourceRowCount": 0, "targetRowCount": 0, "totalDebit": 0.0, "totalCredit": 0.0,
                     "rowCountMatch": true, "amountMatch": true } },
  "docBalanceTest": { "status": "V", "unbalancedDocumentCount": 0,
          "unbalancedDocuments": [ { "documentNumber": "", "debit": 0.0, "credit": 0.0, "diff": 0.0 } ] },
  "infSamplingTest": { "status": "V", "sampleSize": 60, "seed": 48271 },
  "nullRecordsTest": { "status": "V", "nullAccountCount": 0, "nullDocumentCount": 0,
          "nullDescriptionCount": 0, "outOfRangeDateCount": 0,
          "nullRows": [ { "documentNumber": "", "accountCode": "", "postDate": "", "description": "", "issues": ["account"] } ] },
  "resultRef": { "runId": "<32hex>", "generatedUtc": "<ISO-8601>" }
}
```

- **有界的內嵌明細（衍生資料，僅供顯示）**：`completenessTest.diffAccounts`、`docBalanceTest.unbalancedDocuments`、`nullRecordsTest.nullRows` 三者，各自是後端算出的有上限樣本（最多 50 筆），供 UI 展開檢視。借貸不平的明細依差額絕對值排序。空值明細裡的 `issues` 標明該列命中了哪幾項檢查，取值為 `account`、`document`、`description`、`date`。完整性差異明細裡的 `notInTb` 標記的是「這個科目 GL 有、TB 沒有」的情形，也就是把 Not-in-TB 這個概念具名化；若值為 `false`，表示 TB 與 GL 都有這個科目，只是金額對不上。這些明細純粹供顯示，它們不參與任何規則或抽樣計算，也不影響任何 count。要完整列舉 Not-in-TB，仍要靠規劃中的 `query.validationDetailsPage`；這裡的 50 筆只是有界樣本。
- **完整性 part(a)（控制總數核對）**：`completenessTest.partA` 是一道總額層級的端到端核對。它拿投影（staging→target）當下落地的控制總數，包括來源列數、母體列數、母體借方總額與貸方總額，去對上 `target_gl_entry` 的現值。`rowCountMatch` 與 `amountMatch` 是 scaled 整數的比較結果。如果還沒投影、根本沒有控制總數，這幾個鍵仍然齊備，但值為 null 或 false，整體是 `status:"na"` 的形狀。
- 規則狀態的語意依 guide §5 的狀態表。`"V"` 表示已執行且有結果。`"na"` 有兩種來源：一是前置條件不足（缺欄位或缺設定），二是已經執行但 0 筆命中（這種情況 count 仍會回 0 這個數值）。`naReason` 只有在前置條件不足時才會提供文字說明。
- **前置條件**：如果 GL mapping 還沒 commit（沒有 target 投影資料），回 `no_target_data`。但 TB mapping 沒 commit 不算錯誤：這時完整性測試回 `status:"na"`，並在 naReason 裡說明它需要 TB。
- **INF 抽樣公式（可攜、可重現）**：公式是 `ORDER BY (source_row_number * @seed) % 2147483647, entry_id LIMIT @n`，seed 固定為 `48271`、n 預設 60。排序鍵刻意用 `source_row_number`，因為它在批次內是穩定的；不用 AUTOINCREMENT 的 `entry_id`，因為它在重新投影之後會變、不穩定。因此同一批次重跑必然得到相同樣本。seed、樣本的 keys、runId 都會落地到 `result_inf_sampling_test_sample`。guide §4 用「hash」這個措辭描述抽樣，實際就是以這套可攜的整數算術實作（SQLite 與 SQL Server 同義）。如果要變更 RuleSpec 的語意，要先去改 guide。
- **歷史鍵相容**：schema v3 的遷移會清除 v2 時代用舊鍵（`v1`–`v4`）儲存的 `result_rule_run` 摘要。這些摘要是衍生資料，重跑就會恢復，而且結果相同，因為抽樣 seed 是固定的。遷移之後 `project.load.latestRuns` 為 null，前端顯示「未執行」。
- **結果失效不變量**：`result_rule_run` 與 `result_inf_sampling_test_sample` 都是衍生資料。任何改寫它們上游的操作，都必須在同一個 transaction 內把這兩張表清掉，才不會出現「上游資料已經換掉、舊結果卻還留著」這種中間態；而且如果上游 rollback，這個清除也要一併回退。屬於上游的操作包括：GL 與 TB 的 `import.*.fromFile`（不論 replace 或 append）、`mapping.commit.{gl,tb}` 的重投影、`import.accountMapping.fromFile`、`import.authorizedPreparer.fromFile`、以及 `import.{holiday,makeupDay}`。結果一旦失效，`validate.run` 與 `prescreen.run` 就必須重跑才會再有結果。`result_filter_run` 目前還沒持久化（要等到匯出里程碑），但它將來落地時也必須沿用這條不變量。

### Prescreen

規則命名一律使用具體名稱，不再用 R1–R8 這類代號。正準對照見 guide §5 的命名登錄表。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `prescreen.run` | `{}` | 見下方 response 形狀 | **Implemented**。以 SQL set-based 對 `target_gl_entry`（join `staging_calendar_raw_day` / `target_account_mapping`）執行預篩選 summary；完整 response 存入 `result_rule_run`（resume 用），不回任何完整 row list |
| `query.prescreenPage` | `{ projectId, kind, cursor?, pageSize? }` | `{ rows, nextCursor }` | Planned（明細分頁屬匯出/明細里程碑） |

`prescreen.run` response：

```json
{
  "postPeriodApproval": { "status": "V", "naReason": null, "count": 0 },
  "suspiciousKeywords": { "status": "V", "count": 0 },
  "unexpectedAccountPair": { "status": "na", "naReason": "需先匯入科目配對", "count": 0 },
  "trailingZeros": { "status": "V", "count": 0, "zerosThreshold": 3 },
  "creatorSummary": { "status": "V", "naReason": null,
          "creators": [ { "createdBy": "", "entryCount": 0, "debitTotal": 0.0, "creditTotal": 0.0, "manualCount": 0 } ] },
  "rareAccounts": { "status": "V", "distinctAccountCount": 0,
          "accounts": [ { "accountCode": "", "accountName": "", "entryCount": 0, "debitTotal": 0.0, "creditTotal": 0.0 } ] },
  "weekendActivity": { "status": "V", "naReason": null, "postingCount": 0, "approvalCount": 0 },
  "holidayActivity": { "status": "V", "naReason": null, "postingCount": 0, "approvalCount": 0 },
  "blankDescription": { "status": "V", "count": 0 },
  "backdatedPosting": { "status": "V", "count": 0 },
  "nonAuthorizedPreparer": { "status": "na", "naReason": "需先匯入授權編製人員清單", "count": 0 },
  "lowFrequencyPreparer": { "status": "V", "count": 0 },
  "lowFrequencyAccount": { "status": "V", "count": 0 },
  "resultRef": { "runId": "<32hex>", "generatedUtc": "<ISO-8601>" }
}
```

備註：

- 各規則的語意見 guide §5。涵蓋的規則有：期末財報準備日之後才核准；分錄摘要含特定描述（以預設關鍵字比對）；未預期的借貸組合；金額尾端連續為零；編製者彙總；較少使用的科目；週末過帳或核准（會排除補班日）；假日過帳或核准；摘要空白；回溯過帳（過帳日早於傳票日，`voucher_date` 為 NULL 的列不命中）；非授權編製人員（`created_by` 不在授權清單裡，對應 C5）；低頻編製者（`created_by` 在全期間的分錄筆數 ≤ 11，對應 C6）。這些規則回的 counts 一律是 summary，不是 row list。
- 哪些前置條件不足會讓規則落到 `na`，整理如下。`postPeriodApproval`：`docDate` 沒映射，或專案沒有 `lastPeriodStart`。`unexpectedAccountPair`：科目配對沒匯入，或配對表裡缺 `Revenue`，或對方分類（`Receivables`、`Cash`、`Receipt in advance`）全部缺。`creatorSummary`：`createBy` 沒映射。`weekendActivity.approvalCount` 與 `holidayActivity.approvalCount`：當 `docDate` 沒映射時為 `null`（但這兩項的 `postingCount` 永遠算得出來，因為 `postDate` 是必填的 mapping）。`holidayActivity`：還沒有假日資料。`nonAuthorizedPreparer`：授權編製人員清單沒匯入，也就是 `target_authorized_preparer` 是空的。要注意 0 筆命中同樣會標成 `na`（依 guide §5 狀態表），但 count 仍回 0。`lowFrequencyPreparer` 則沒有前置條件，永遠會跑，回的是 `{ status, count }`、沒有 `naReason`。
- `creatorSummary` 與 `rareAccounts` 是彙總規則，不是逐列打標（row tag），各自最多回 50 列；`rareAccounts.accounts` 依使用次數升冪排序。`lowFrequencyAccount`（R12）是 `rareAccounts`（也就是 R6 彙總）的列述詞版本，可以拿來當進階篩選的列述詞，述詞是 `account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= @n)`，固定的預設門檻是 11。
- 連續零尾數的門檻是動態推估出來的：取借方金額的平均值（這個平均用 C# 的 decimal 從 scaled 值換算，不拿 SQLite 的 REAL `AVG` 當權威），再算 `max(3, floor(log10(avg)) − 1)`；`zerosThreshold` 回報的就是這一次實際採用的門檻值。進階篩選裡的 `customTrailingZeros` 條件（即原本的 A4 語意）可以指定一個固定位數，取代這個動態值。
- 自訂關鍵字（原本的 A2 語意）由 filter 的 `customKeywords` 條件涵蓋；自訂科目配對（原本的 A3 語意）由 filter 的 `accountPair` 條件涵蓋。兩者詳見 Filter / Criteria 章節。
- **歷史鍵相容**：schema v3 的遷移會清除 v2 時代用舊鍵（`r1`–`r8`、`descNullCount`）儲存的摘要，重跑即可恢復。至於 `config_filter_scenario` 裡的舊 `prescreenKey`，遷移會逐一翻譯成新鍵並保留下來，對照是：`r1→postPeriodApproval`、`r2→suspiciousKeywords`、`r4→trailingZeros`、`r7post→weekendPosting`、`r7doc→weekendApproval`、`r8post→holidayPosting`、`r8doc→holidayApproval`、`descNull→blankDescription`。

### Filter / Criteria

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `filter.preview` | `{ scenario }` | `{ scenario: { name, count, voucherCount, previewRows } }` | **Implemented**。後端將條件 AST 轉成**參數化 SQL**（識別字只來自欄位白名單）交由 DB set-based 評估；`previewRows` ≤ 50（`{ documentNumber, lineItem, postDate, accountCode, accountName, documentDescription, amount, drCr }`）。**本版為無狀態查詢**：不落地結果——與原規劃差異：`result_filter_run` / `resultRef` 延後到匯出里程碑，屆時 export 以同一 AST set-based 重跑 |
| `query.filterPage` | `{ projectId, runId?, cursor?, pageSize? }` | `{ rows, nextCursor }` | Planned（隨 `result_filter_run` 一起實作） |
| `filter.commit` | `{ scenarios }` | `{ ok, savedCount }` | **Implemented**。保存情境**定義**（非結果）到 `config_filter_scenario`：replace-all 語意、上限 **10** 個、名稱不可重複、每個情境重新驗證；`project.load` 以 `filterScenarios` 回傳 resume。原規劃的 `savedRef` 延後到匯出里程碑 |

下面是 `scenario` 條件 AST 的 schema。前端的 Query Builder 只負責組裝這份 JSON，它本身不評估任何規則：

```json
{
  "source": "kct（選填；省略/null/未知值 = 查核員自擬情境，行為不變）",
  "name": "情境名稱（非 KCT 來源時必填）",
  "rationale": "篩選動機說明（非 KCT 來源時必填；保留到工作底稿）",
  "groups": [
    {
      "join": "AND",
      "rules": [
        {
          "join": "AND",
          "type": "prescreen | text | dateRange | numRange | drCrOnly | manualAuto | accountPair | specialAccountCategoryPair | periodInOut | customKeywords | customTrailingZeros | customPreparerEntryCount | customAccountEntryCount | revenueDebitNearQuarterEnd | revenueWithoutNormalCounterpart | manualRevenueEntry | trailingDigits | preparerEqualsApprover",
          "prescreenKey": "postPeriodApproval | suspiciousKeywords | unexpectedAccountPair | trailingZeros | weekendPosting | weekendApproval | holidayPosting | holidayApproval | blankDescription | backdatedPosting | nonAuthorizedPreparer | lowFrequencyPreparer | lowFrequencyAccount",
          "field": "docNum | lineID | accNum | accName | description | jeSource | createBy | approveBy | postDate | docDate | voucherDate | amount",
          "keywords": "逗號分隔的關鍵字",
          "mode": "contains | exact | notContains | notExact",
          "from": "",
          "to": "",
          "drCr": "debit | credit",
          "isManual": "true | false",
          "pairMode": "exact | debitAnchor | creditAnchor (accountPair) ｜ drAndCr | drNotCr | notDrCr (specialAccountCategoryPair)",
          "debitCategory": "Receivables | Cash | Receipt in advance | Revenue | Others",
          "creditCategory": "Receivables | Cash | Receipt in advance | Revenue | Others",
          "inPeriod": "true | false",
          "digits": 3,
          "maxEntries": 11,
          "windowDays": 5
        }
      ]
    }
  ]
}
```

AST 語意與安全約束：

- **結合律**：不論是同一群組內的規則之間，還是群組與群組之間，結合都採左折疊、累積加括號，也就是 `((c1 OP c2) OP c3)` 這種形式。第一條規則、以及第一個群組的 `join` 會被忽略。
- **source（選填的來源標記，2026-06-23 加入）**：用來標明這個情境是查核員自己擬的，還是來自固定的方法論清單。它唯一具名的值是 `"kct"`，代表 KCT 小組方法論檢核條件。省略、null、或任何其他值，一律當成查核員自擬的情境處理，行為與過去完全相同。當 `source:"kct"` 時，`name` 與 `rationale` 兩個欄位豁免必填，因為 KCT 條件是一份固定的查核清單、不是查核員逐項撰寫的，所以 UI 不會向使用者索取名稱與動機。不過留痕不能是空的：`filter.commit` 落地時，如果是 KCT 來源、而 `name` 或 `rationale` 留白，後端會補上一個穩定的非空替補值（`rationale` 補成「KCT 小組方法論檢核條件」）。因此 `config_filter_scenario.name` 與 `.rationale` 永遠不會是空的，`project.load.filterScenarios` 回放出來的名稱與動機也不會是空的。其他來源不做這種替補。
- **prescreenKey** 只接受 row-tag 類型的規則，也就是上面列的那十二個鍵。`creatorSummary` 與 `rareAccounts` 是彙總規則，不能拿來當列述詞。`unexpectedAccountPair` 需要科目配對已匯入，否則回 `invalid_scenario`。`nonAuthorizedPreparer` 需要授權編製人員清單已匯入；在 filter 這一端，空名單會回 `invalid_scenario`，這道閘控刻意鏡像 `unexpectedAccountPair` 的 validator。除了 validator，述詞層自己還有一道自保：它用 `EXISTS (SELECT 1 FROM target_authorized_preparer) AND …` 包住，所以即使有人繞過了 validator，只要名單是空的，整個述詞就是 FALSE、零命中，這與 `prescreen.run` 的 `na` 語意一致；它不會因為寫成 `NOT IN (空集合)` 而反轉成全部命中。`lowFrequencyPreparer` 則沒有前置條件。prescreen 類的條件與 `prescreen.run` 共用同一份 SQL 述詞（單一事實來源，落在 Infrastructure 的 `GlRulePredicates`），都是即時計算，不依賴先前任何一次 run 的結果。
- **field 白名單**：`field` 是邏輯 id，經由 Domain 的白名單映射到實體欄位，例如 `docNum→document_number`、`docDate→approval_date`、`voucherDate→voucher_date`、`amount→ABS(amount_scaled)` 等。未知的 id 回 `invalid_scenario`。這裡有一條安全鐵律：SQL 的識別字永遠不來自使用者輸入；所有的值（關鍵字、日期、金額、分類、位數）一律走參數綁定。
- **legacy 預篩選的遷移對照**（從 vba-1120 ServiceFilter 的 12 條件對應到本 AST）：週末或假日的過帳/核准，對應到 `prescreen` 的 `weekendPosting`、`weekendApproval`、`holidayPosting`、`holidayApproval`（補班日的排除已內建在週末規則裡）；僅借方或僅貸方對應 `drCrOnly`；人工分錄對應 `manualAuto`；關鍵字對應 `text`；日期或數值區間對應 `dateRange` 與 `numRange`。
- `accountPair`（科目配對分析，三模式見 guide §6.1，即原本的 A3 語意）：需要科目配對已匯入，否則回 `invalid_scenario`；UI 會鏡像這個條件去隱藏按鈕，但權威判斷在後端。三個模式所需的欄位不同：`pairMode:"exact"` 需要 `debitCategory` 與 `creditCategory` 兩者都填；`"debitAnchor"` 只需要 `debitCategory`；`"creditAnchor"` 只需要 `creditCategory`。分類值必須落在 guide §2.3 的白名單內。借貸側的判定是：借方側等於「指定的借方分類，且 `amount_scaled >= 0`」，貸方側等於「指定的貸方分類，且 `amount_scaled < 0`」；金額為 0 元的歸到借方側，這與 `drCr` 的推導一致。錨定模式（anchor）的輸出，是錨定的那筆分錄，加上同一張傳票裡對方側的分錄。
- `specialAccountCategoryPair`（考量特殊科目類別配對，採顯式雙類別加上否定語意，2026-06-23 加入）：這是 `accountPair` 的姊妹條件。查核員選一個借方類別 A（填 `debitCategory`）、一個貸方類別 B（填 `creditCategory`），再選三個模式之一，用來標記出帶有該借貸類別配對（含「不存在這種配對」）的傳票或分錄。借貸側的判定與 `accountPair` 相同：A 借等於「`amount_scaled >= 0` 且分類為 A」，B 貸等於「`amount_scaled < 0` 且分類為 B」。三個模式都要求 `debitCategory` 與 `creditCategory` 兩者都填、都在 guide §2.3 白名單內、且科目配對已匯入；連否定模式也需要 B 與 A 才能判定「不存在」。以下 `pairMode` 的取值都以一張傳票（`document_number`）為判定單位：
  - `drAndCr`（借 A 且貸 B）：這張傳票同時有 A 借列與 B 貸列。命中時標記「A 借列或 B 貸列」。
  - `drNotCr`（借 A 且貸非 B）：這張傳票有 A 借列，但沒有任何 B 貸列（以 `NOT EXISTS` 判定）。命中時標記 A 借列。
  - `notDrCr`（借非 A 且貸 B）：這張傳票有 B 貸列，但沒有任何 A 借列（以 `NOT EXISTS` 判定）。命中時標記 B 貸列。
  以上都是純 ANSI 的寫法（`EXISTS` 與 `NOT EXISTS`，分類值參數綁定），所以 SQLite 與 SQL Server 由構造上就等價。這裡有一個刻意的取捨：`drAndCr` 的 SQL 邏輯其實與 `accountPair` 的 `exact` 模式重疊，但這兩者是面向使用者的不同條件（模式標籤不同、否定語意也不同），所以這份重複是刻意保留的。
- `periodInOut`（期內或期外，見 guide §6.2）：`inPeriod:"true"` 表示 `post_date` 落在專案的會計期間內（含邊界），`"false"` 表示落在期間外。`post_date` 為 NULL 的列，兩側都不命中。這個條件沒有前置條件，因為會計期間是專案的必填欄位。
- `customKeywords`（自訂關鍵字，即原本的 A2 語意）：述詞與 `suspiciousKeywords` 相同（contains-any、不分大小寫、NULL 以空字串參與比對），差別只在關鍵字改由使用者輸入（逗號分隔，至少要有一個非空白）。
- `customTrailingZeros`（自訂尾數位數，即原本的 A4 語意）：`digits` 是 1–12 的整數，指主單位末端連續為零的位數，用它取代動態門檻；述詞與 `trailingZeros` 相同（用整數取模判定，不用字串函式）。
- `customPreparerEntryCount`（自訂低頻編製者門檻，C6 的自訂軌）：`maxEntries` 是 ≥ 1 的整數，用它取代固定預設的 11；述詞與 `lowFrequencyPreparer` 相同（`created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= @n)`，門檻參數綁定）。
- `customAccountEntryCount`（自訂低頻科目門檻，C9 的自訂軌）：`maxEntries` 是 ≥ 1 的整數，用它取代固定預設的 11；述詞與 `lowFrequencyAccount` 相同（`account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= @n)`，門檻參數綁定）。
- **KCT 小組條件（2026-06-23，Phase 1）**：下列五個型別是 KCT 小組方法學清單專屬的條件，前端把它們歸在獨立的「KCT 小組條件」分組底下。它們各有獨立的 wire 型別，述詞主要只讀 `target_gl_entry`（其中部分還會讀 `target_account_mapping`）。識別字一樣只出自欄位白名單或常數，使用者輸入的值（天數、尾數）一律參數綁定。
  - `revenueDebitNearQuarterEnd`（季末前借記收入，清單 A）：科目分類為 Revenue、且在借方側（`amount_scaled >= 0`），同時 `post_date` 要落在任一曆年季底（3/31、6/30、9/30、12/31）往前推 `windowDays` 天（含季底當天）所構成的視窗內。這些視窗是後端依專案查核期間加上 `windowDays` 枚舉出來的，視窗邊界參數綁定。`windowDays` 是 1–92 的整數。需要科目配對已匯入，否則回 `invalid_scenario`。
  - `revenueWithoutNormalCounterpart`（收入無一般對方科目，清單 C）：本列是 Revenue 貸方（`amount_scaled < 0`），但它所在的那張傳票裡，沒有任何一筆「在借方側、且分類屬於 {Receivables, Receipt in advance}」的分錄。注意現金（Cash）不算一般對方科目。需要科目配對已匯入。
  - `manualRevenueEntry`（收入之人工分錄，清單 D）：科目分類為 Revenue、且 `is_manual = 1`（來源沒提供人工旗標的列永遠不命中，這點與 `manualAuto` 相同）。需要科目配對已匯入。
  - `trailingDigits`（特定金額尾數，清單 H）：把顯示金額的主單位取整數（算法是 `ABS(amount_scaled) / MoneyScale` 並捨去小數），它末 k 位若等於某個指定的尾數樣態就算命中，多個樣態中任一符合即命中（且要求 `amount_scaled <> 0`）。樣態清單刻意重用 `keywords` 欄位來傳（逗號分隔，每組是 1–12 位的純數字，例如 `999999`、`000000`）。計算用整數除法與取模，兩個 provider 等價。
  - `preparerEqualsApprover`（編製與核准為同一人，清單 J）：要求 `created_by` 與 `approved_by` 都非空白，且兩者相等（比較時忽略大小寫與前後空白）。`createBy` 或 `approveBy` 沒配對時，零命中。
- **KCT 重用既有型別（清單 E/F/G/I）**：這幾項不新增 wire 型別。前端的「KCT 小組條件」分組改用預設按鈕，帶入既有型別的預填規則：特定人員用 `text`（`createBy`、`exact`）；特定摘要用 `customKeywords`；空白摘要用 `prescreen`（`blankDescription`）；非營業日則組成一個群組 `prescreen weekendPosting OR prescreen holidayPosting`（兩者都比對 `post_date`）。送到後端就是既有型別，契約沒有任何變動。
- `numRange`：`from` 與 `to` 是顯示值的 decimal，後端會先用 MoneyScale 轉成 scaled，再去比較 `ABS(amount_scaled)`；兩個邊界至少要填一個。`dateRange`：日期是 `yyyy-MM-dd`，比較的是 `field` 指定的那個日期欄；兩個邊界至少要填一個。
- `text`：NULL 欄位以空字串參與比對（用 `COALESCE`，所以 `notContains` 對 NULL 列會成立）；比對不分大小寫。
- `manualAuto`：比對 `is_manual = 1|0`；來源沒提供人工旗標（也就是 NULL）的列永遠不匹配。
- 以下任何一種情況都會讓 AST 驗證失敗、回 `invalid_scenario`（訊息會把所有錯誤合併列出）：缺名稱或動機（非 KCT 來源時這兩項必填；`source:"kct"` 豁免這兩項，其餘檢查照舊）；空群組；未知的 field、prescreenKey、type 或 mode；缺邊界；用了 `prescreen postPeriodApproval` 但專案沒有 `lastPeriodStart`；用了 `accountPair`、`unexpectedAccountPair` 或 `specialAccountCategoryPair` 但科目配對未匯入；`digits` 超出 1–12；`customPreparerEntryCount` 或 `customAccountEntryCount` 的 `maxEntries` 小於 1；用了 KCT 的 `revenueDebitNearQuarterEnd`、`revenueWithoutNormalCounterpart` 或 `manualRevenueEntry` 但科目配對未匯入；`windowDays` 超出 1–92；`trailingDigits` 的樣態不是 1–12 位的純數字；`specialAccountCategoryPair` 的 `pairMode` 不是 `drAndCr`、`drNotCr`、`notDrCr` 三者之一；`specialAccountCategoryPair` 缺 `debitCategory` 或 `creditCategory`；分類值不在白名單內。另外兩種 commit 階段的錯誤：commit 超過 10 個情境回 `scenario_limit_reached`；commit 出現重名回 `invalid_scenario`。

### Export

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `export.validation` | `{}` | `{ ok, message }` | Current stub：validation 匯出仍委派前端；scale-complete export 需走 backend-controlled path |
| `export.prescreen` | `{}` | `{ ok, message }` | Current stub：prescreen 匯出仍委派前端；scale-complete export 需走 backend-controlled path |
| `export.criteria` | `{}` | `{ ok, message }` | Current stub：criteria 匯出仍委派前端；scale-complete export 需走 backend-controlled path |
| `export.workpaper` | `{ selected }` | `{ ok, message }` | Current stub：workpaper 匯出仍委派前端；正式 large-data path 必須改為 backend streaming writer |

## JetApi Typed Facade

前端呼叫 bridge 的唯一管道是 `window.JetApi.*`。這個 facade 目前是手動維護的，放在 `wwwroot/js/jet-api.js`；它以檔內的 `SUPPORTED_ACTIONS` 清單為單一事實來源，據此自動生成各個 typed method。目前還沒有 C# 版的 `JetBridgeScriptFactory`；將來就算建了 script factory，生成規則也不會變。action 名稱對應到 facade method 的規則如下：

1. 以 `.` 把 action 名切成數段。
2. 第一段全部小寫，後續每段的首字母大寫，再串接起來（也就是 lowerCamelCase）。
3. 例如：`validate.run` 變成 `JetApi.validateRun`；`mapping.commit.gl` 變成 `JetApi.mappingCommitGl`。

下表是目前已註冊的 method，同樣以 `wwwroot/js/jet-api.js` 的 `SUPPORTED_ACTIONS` 為單一事實來源；其中 dev.db.* 與 dev.log.* 只在 Debug 組建才有：

| Action | JetApi method |
|:---|:---|
| `system.ping` | `JetApi.systemPing` |
| `project.list` | `JetApi.projectList` |
| `project.saveProgress` | `JetApi.projectSaveProgress` |
| `project.create` | `JetApi.projectCreate` |
| `project.load` | `JetApi.projectLoad` |
| `project.delete` | `JetApi.projectDelete` |
| `project.loadDemo` | `JetApi.projectLoadDemo` |
| `demo.exportGlFile` | `JetApi.demoExportGlFile` |
| `demo.exportTbFile` | `JetApi.demoExportTbFile` |
| `demo.exportAccountMappingFile` | `JetApi.demoExportAccountMappingFile` |
| `demo.exportAuthorizedPreparerFile` | `JetApi.demoExportAuthorizedPreparerFile` |
| `import.gl.fromFile` | `JetApi.importGlFromFile` |
| `import.tb.fromFile` | `JetApi.importTbFromFile` |
| `import.accountMapping.fromFile` | `JetApi.importAccountMappingFromFile` |
| `import.authorizedPreparer.fromFile` | `JetApi.importAuthorizedPreparerFromFile` |
| `import.inspectFile` | `JetApi.importInspectFile` |
| `import.previewFile` | `JetApi.importPreviewFile` |
| `import.holiday` | `JetApi.importHoliday` |
| `import.makeupDay` | `JetApi.importMakeupDay` |
| `import.holiday.fromFile` | `JetApi.importHolidayFromFile` |
| `import.makeupDay.fromFile` | `JetApi.importMakeupDayFromFile` |
| `calendar.setNonWorkingDays` | `JetApi.calendarSetNonWorkingDays` |
| `mapping.autoSuggest` | `JetApi.mappingAutoSuggest` |
| `mapping.commit.gl` | `JetApi.mappingCommitGl` |
| `mapping.commit.tb` | `JetApi.mappingCommitTb` |
| `validate.run` | `JetApi.validateRun` |
| `prescreen.run` | `JetApi.prescreenRun` |
| `filter.preview` | `JetApi.filterPreview` |
| `filter.commit` | `JetApi.filterCommit` |
| `query.dataPreview` | `JetApi.queryDataPreview` |
| `query.completenessDiffPage` | `JetApi.queryCompletenessDiffPage` |
| `query.docBalancePage` | `JetApi.queryDocBalancePage` |
| `query.nullRecordsPage` | `JetApi.queryNullRecordsPage` |
| `query.filterHitsPage` | `JetApi.queryFilterHitsPage` |
| `query.infSamplePage` | `JetApi.queryInfSamplePage` |
| `query.tagMatrixScenarios` | `JetApi.queryTagMatrixScenarios` |
| `query.tagMatrixVoucherPage` | `JetApi.queryTagMatrixVoucherPage` |
| `query.tagMatrixRowPage` | `JetApi.queryTagMatrixRowPage` |
| `log.append` | `JetApi.logAppend` |
| `log.recent` | `JetApi.logRecent` |
| `host.selectFile` | `JetApi.hostSelectFile` |
| `host.selectFiles` | `JetApi.hostSelectFiles` |
| `host.selectSavePath` | `JetApi.hostSelectSavePath` |
| `host.openFolder` | `JetApi.hostOpenFolder` |
| `host.exitApp` | `JetApi.hostExitApp` |
| `export.workpaperStream` | `JetApi.exportWorkpaperStream` |
| `dev.db.overview` | `JetApi.devDbOverview` |
| `dev.db.tableData` | `JetApi.devDbTableData` |
| `dev.log.export` | `JetApi.devLogExport` |

規則：

- UI、demo、workflow 的程式碼一律用 `await JetApi.xxx(payload)` 呼叫，不得直接呼叫 `window.jet.invoke(...)` 或 `window.chrome.webview.postMessage(...)`；唯一的例外是 bootstrap script 本身。
- 呼叫一個沒註冊的 method 會得到 undefined function error，這正好提示你要先在這份 manifest 新增對應的 action。
- 新增 action 的順序固定是：先改這份 manifest，再改 `SUPPORTED_ACTIONS`，再改 handler，最後才在 UI 裡使用 `JetApi.<newMethod>`。

## Error Codes

Handler 用 `JetActionException(code, message)` 回報業務錯誤；bridge 會把其中的 `code` 直接放進 response 的 `error.code`。除此之外的任何未預期例外，一律以 `bridge_error` 呈現。已註冊的錯誤碼如下：

| Code | 意義 |
|:---|:---|
| `invalid_payload` | payload 缺必填欄位或格式錯誤（訊息列出欄名） |
| `no_active_project` | 尚未建立或載入任何專案 |
| `project_not_found` | projectId 不存在或格式無效 |
| `file_not_found` | filePath 指向的檔案不存在；或 dev.db.* 檢視時專案 DB 檔不存在（唯讀檢視不會建檔） |
| `unsupported_file_type` | 副檔名不在支援清單（`.xlsx`、`.csv`、`.txt`） |
| `file_read_error` | 檔案無法開啟或解析（如被 Excel 鎖定、損壞、文字檔編碼不可解碼） |
| `sheet_not_found` | `sheetName` 指定的工作表不存在於 `.xlsx` 檔案中 |
| `empty_workbook` | 無工作表、無標頭列（含 CSV 空檔／僅標頭），或匯入後資料列數為 0 |
| `no_import_batch` | 尚未匯入該 dataset 就執行 mapping commit，或尚無批次就以 `mode:"append"` 附加來源 |
| `column_mismatch` | `mode:"append"` 的來源欄名集合與既有批次不一致（訊息列出雙向差集） |
| `missing_required_mapping` | mapping 缺必填 key（訊息列出缺漏 keys） |
| `mapping_column_not_found` | mapping 指到的欄位不在匯入批次 columns 中 |
| `projection_failed` | staging→target 轉換有列級錯誤（訊息含錯誤數與前 10 筆明細；整批已 rollback） |
| `unsupported_mode` | import mode 或 amountMode/changeMode 不被支援 |
| `table_not_allowed` | dev.db.tableData 的 tableName 不在白名單 |
| `no_target_data` | 尚未 commit GL mapping（無 target 投影資料）就執行 `validate.run` / `prescreen.run` / `filter.preview`。與 `no_import_batch`（mapping commit 階段缺匯入批次）語意區隔 |
| `invalid_scenario` | filter 條件 AST 驗證失敗（缺名稱/動機（非 KCT 來源時必填；`source:"kct"` 豁免）、空群組、未知 field/prescreenKey/type/mode、缺邊界、科目配對未匯入即用 accountPair/unexpectedAccountPair、digits 超界、KCT 條件前置/參數不符、分類不在白名單、commit 重名…；訊息合併列出全部錯誤） |
| `scenario_limit_reached` | `filter.commit` 超過 10 個情境上限 |
| `gl_amounts_all_zero` | `mapping.commit.gl` 投影出非空 GL 母體，但整個母體借貸總額皆為 0（常見於借/貸金額欄誤配到「傳票總額」欄——同一張傳票每列借=貸、逐列淨額恆為 0——或配到空欄）；整批已 rollback，請改配對列層借/貸金額欄 |
| `bridge_error` | 其他未分類錯誤（fallback，含未知 action） |

## Demo Pipeline 對齊原則

Demo 的載入流程必須走與使用者實際上傳完全相同的 file-based import pipeline，不得再退回用 row-based 的 demo fallback。目前 MockDataLoader 已經實作了步驟 1 到 6（其中包含 accountMapping 匯入，這個格式是固定的、沒有獨立的 commit 步驟），以及步驟 7 裡的 `validate.run`、`prescreen.run`、`filter.preview`、`filter.commit`（步驟 7 的明細分頁 `query.*Page` 仍是 Planned）。完整流程如下：

1. `JetApi.projectLoadDemo()` → 取 metadata（專案欄位、file names、holidays、makeup、建議 mapping）。
2. `JetApi.demoExportGlFile()` / `JetApi.demoExportTbFile()` / `JetApi.demoExportAccountMappingFile()` → 取得 host 端 `.xlsx` 檔案路徑。
3. `JetApi.projectCreate(metadata)`。
4. `JetApi.importGlFromFile({ filePath, fileName })` / `JetApi.importTbFromFile(...)` / `JetApi.importAccountMappingFromFile(...)`。
5. `JetApi.importHoliday({ dates })` / `JetApi.importMakeupDay({ dates })`。
6. `JetApi.mappingCommitGl({ mapping })` / `JetApi.mappingCommitTb({ mapping })`，以及任何未來已在本 manifest 登記的 account/classification commit action。
7. `JetApi.validateRun()` / `JetApi.prescreenRun()`；filter 步驟以 `DemoProjectDto.demoScenario` 走 `JetApi.filterPreview({ scenario })` → `JetApi.filterCommit({ scenarios })`。明細分頁（`query.validationDetailsPage` / `query.prescreenPage` / `query.filterPage`）屬未來里程碑。

`demo.fetch*Rows` 與 row-based 的 `import.*` 只保留作 legacy fallback。正式 UI、測試按鈕、文件範例與新程式碼，都不得拿它們來充當 demo pipeline。

## Current Logical Mapping Keys

### GL Mapping Keys

下表的這些 key 由 `docs/jet-template.html` 與 C# handler 共同使用：

| Key | Label | Required | Notes |
|:---|:---|:---|:---|
| `docNum` | 傳票號碼 | Yes | 憑證聚合主鍵 |
| `lineID` | 傳票文件項次 | Conditional | 實際 ERP 匯出（如目前 fixture）常無項次欄；缺漏時 `target_gl_entry.line_item` 為 NULL，`docNum` 仍是憑證聚合鍵 |
| `postDate` | 總帳日期 | Yes | validation / filter |
| `docDate` | 傳票核准日 | No | 期末後核准、週末/假日核准等日期類規則 |
| `voucherDate` | 傳票日期 | No | 回溯過帳偵測(過帳日 < 傳票日)、日期區間篩選;選填 |
| `accNum` | 會計科目編號 | Yes | validation / prescreen / filters |
| `accName` | 會計科目名稱 | Yes | UI / reporting |
| `description` | 傳票摘要 | Yes | 摘要關鍵字規則 / 文字篩選 |
| `jeSource` | 分錄來源模組 | No | UI only today |
| `createBy` | 傳票建立人員 | No | 編製者彙總 |
| `approveBy` | 傳票核准人員 | No | UI only today |
| `manual` | 人工/自動分錄 | No | manualAuto filter |
| `amount` | 傳票金額（單欄） | Conditional | 與 debit/credit 雙欄位互斥 |
| `debitAmount` | 借方金額 | Conditional | 雙欄位模式 |
| `creditAmount` | 貸方金額 | Conditional | 雙欄位模式 |
| `dcField` | 借貸別欄位 | No | optional UI mapping key |
| `dcDebitCode` | 借方標識代碼 | No | optional UI mapping key |

### TB Mapping Keys

| Key | Label | Required | Notes |
|:---|:---|:---|:---|
| `accNum` | 會計科目編號 | Yes | completeness diff |
| `accName` | 會計科目名稱 | Yes | UI only today |
| `amount` | 年度變動金額 | Conditional | DirectChange mode（`changeMode: "direct"`） |
| `debitAmt` | 借方金額 | Conditional | DebitCredit change mode（`changeMode: "debitCredit"`，變動 = 借方 − 貸方）— supported |
| `creditAmt` | 貸方金額 | Conditional | DebitCredit change mode — supported |

> TB 的金額表示法是條件式的，兩種擇一：要嘛用 `amount` 單欄（direct 模式），要嘛用 `debitAmt` 加 `creditAmt` 雙欄（debitCredit 模式）。至於 OpenClose 與 OpenCloseBySide 兩種模式（見 guide §2.2），目前還沒有對應的 mapping key，列為未來的契約擴充。

## Step Data Outline

這份綱要是前端在生成 UI 之前，應該先對齊的資料模型。

前端的步驟模型是 6 步，依序為：建立案件 → 匯入資料 → 欄位配對 → 資料驗證與測試 → 進階條件篩選 → 匯出底稿。其中「資料驗證與測試」這一步把 validation 與 prescreen 合併在一起，這是為了對齊 `docs/jet-template.html` 的 step3 設計。這個合併安排是在 2026-06-10 定案的，當時是為了收斂「七步骨架」與「五步規格」兩種說法之間的不一致。

| Step | 前端需要的資料 | 建議 action |
|:---|:---|:---|
| Step 0 Shell | app name, DB provider, supported actions, demo enabled | `app.bootstrap`, `system.ping` |
| Step 1 Project / Import | project metadata, import file names, streaming import columns, holidays, makeup days | `project.create`, `import.*.fromFile`, `project.loadDemo` |
| Step 2 Mapping | GL/TB field definitions, uploaded columns, suggested mappings, committed mappings | `mapping.autoSuggest`, `mapping.commit.gl`, `mapping.commit.tb` |
| Step 3 資料驗證與測試 | stats、四項資料驗證狀態物件、九項預篩選狀態物件、resultRef（counts 渲染為徽章；na 顯示 `—`；規則以中文名呈現，不用代號） | `validate.run`, `prescreen.run`（明細分頁 Planned：`query.validationDetailsPage` / `query.prescreenPage`） |
| Step 4 進階條件篩選 | 條件 AST 草稿（Query Builder 本地組裝）、預覽 `{ count, voucherCount, previewRows ≤50 }`、已儲存情境清單 ≤10、科目配對 presence（決定 accountPair 條件是否顯示） | `filter.preview`, `filter.commit`（`query.filterPage` Planned） |
| Step 5 Export | selected outputs, export feedback | `export.validation`, `export.prescreen`, `export.criteria`, `export.workpaper` |

## Change Process For New UI Or New Actions

當 agent 被要求新增畫面、重做 UX、或擴充 bridge 時，要依下列順序進行：

1. 明確指出影響哪一個 workflow step。
2. 先檢查現有 action 是否已足夠。
3. 若不足，先在本 manifest 補齊：
   - action name
   - payload shape
   - response shape
   - owner layer
   - UI caller / fixed bindings
4. 再修改 `ActionDispatcher`、DTO、handler、HTML。
5. 若契約變動會影響 `docs/jet-guide.md`，同步更新。

## Anti-Patterns

- 先生成很完整的 UI，事後才補 action 契約
- 在 HTML 裡拼 SQL 或內嵌業務規則
- 把 bridge 當 application service 寫
- 改了 action payload，卻不更新 manifest
- 對同一需求同時發明 `query.*`、`load.*`、`fetch.*` 三種名稱空間
- 在前端實作 authoritative 的 validation / prescreen / filter 規則（必須走 handler）
- UI code 直接呼叫 `window.jet.invoke('xxx', payload)` 或 `window.chrome.webview.postMessage(...)`；一律改走 `JetApi.*`
- 同一條業務規則在 HTML/JS 與 C# handler 各寫一份（必然發散）
- Demo/test path 使用 `demo.fetch*Rows` 或 row-based `import.*` 模擬正式 pipeline
- **Bridge payload / response 攜帶超過 1000 筆明細 row**（大型 GL 母體會炸 JS 端與 postMessage；違反 `docs/jet-guide.md` §1.5）
- **在 Application/Bridge 層對 GL/TB row 集合做 LINQ 計算 V/R/Filter 規則**（必須由 DB 引擎 set-based 處理；違反 §1.5.2）

## Scale-First Contract Baseline

本章定義正式契約的基準。原則有四條：匯入時傳的是檔案路徑、規則執行回的是 summary 加上 `resultRef`、明細一律走 keyset paging、而且 Bridge 的 payload 與 response 都不搬運完整的 GL 或 TB row set。完整背景見 `docs/jet-guide.md` §1.5。

### Ingest 契約基準

| 動作 | Payload | Response | 執行要求 |
|:---|:---|:---|:---|
| `import.gl.fromFile` | `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | 後端透過 `ITabularFileReader` streaming 讀檔，直接 bulk insert 進 staging；payload 不帶 rows |
| `import.tb.fromFile` | `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | 同 GL 匯入語意；payload 不帶 rows |
| `import.accountMapping.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, columns, fileName, importedUtc }` | 讀取科目配對來源檔並寫入 staging＋投影 target；payload 不帶 rows |
| `import.authorizedPreparer.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, fileName, importedUtc }` | 讀取單欄授權編製人員清單（`.xlsx`）寫入 staging＋投影 `target_authorized_preparer`；payload 不帶 rows |

Row-based 的 `import.gl`、`import.tb`、`import.accountMapping` 只作 legacy compatibility 用，不屬於正式 UI、demo 與測試 pipeline，或新程式碼的實作路徑。

### Query 契約基準（Result Reference + Paging）

| 動作 | Response 基準 | 明細讀取 |
|:---|:---|:---|
| `validate.run` | `{ stats, 四項資料驗證狀態物件, resultRef }`（Implemented，見 Validation 章節） | `query.validationDetailsPage`（Planned） |
| `prescreen.run` | `{ 九項預篩選狀態物件, resultRef }`（Implemented，見 Prescreen 章節） | `query.prescreenPage`（Planned） |
| `filter.preview` | `{ scenario: { name, count, voucherCount, previewRows } }`，`previewRows` ≤ 50；本版無狀態、無 `resultRef`（`result_filter_run` 隨匯出里程碑落地後恢復） | `query.filterPage`（Planned） |
| `filter.commit` | `{ ok, savedCount }`（Implemented；`savedRef` 隨匯出里程碑） | — |

### 分頁與匯出動作

| 動作 | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `query.glPage` | `{ projectId, cursor, pageSize, sort? }` | `{ rows[], nextCursor }` | GL keyset paging（給 step 1 預覽 / step 5 工作底稿明細） |
| `query.validationDetailsPage` | `{ projectId, kind, cursor, pageSize }` | `{ rows[], nextCursor }` | 資料驗證明細分頁（`kind` 用命名登錄表的 wire key） |
| `query.prescreenPage` | `{ projectId, ruleKey, cursor, pageSize }` | `{ rows[], nextCursor }` | 預篩選規則明細分頁（`ruleKey` 用命名登錄表的 wire key） |
| `query.filterPage` | `{ projectId, scenarioId, cursor, pageSize }` | `{ rows[], nextCursor }` | 自訂篩選明細分頁 |
| `export.workpaperStream` | `{ sheets?, outputPath? }` | `{ ok, outputPath, bytesWritten, sheetStats: [{ sheetName, rowsWritten }] }` | 走 OpenXML SAX writer 直接串流寫出底稿 `.xlsx`。**`outputPath` 選填**：省略時**直接落專案目錄**,檔名 `{公司名}_{yyyyMMddHHmmss}_WorkingPaper.xlsx`(前端預設走此,匯出後以 `host.openFolder` 揭示);給定時寫到該路徑(匯出到其他位置 / 測試確定性落點)。實際落點一律回在 `response.outputPath`。`sheets` 省略=匯出全部工作表,否則只匯出指定的工作表名子集。`bytesWritten` 為寫出位元組數、`sheetStats` 為每張工作表的 `{ sheetName, rowsWritten }`(rowsWritten 為資料列數)。資料表 keyset 逐頁串流、不全載入(guide §1.5)。匯出前 handler 先以同源 materializer 把全部已存篩選情境的命中落地 `result_filter_run`(step3/4/4-1 矩陣由它即時 pivot,故惰性補算屬 handler 編排、writer 不反向依賴 Application)。**Implemented**(E1 Task 6;落專案目錄與 host.openFolder 為 GUI 驗收後修正) |

### Result Reference 概念

`resultRef` 的形狀是 `{ projectId, runId, generatedUtc }`。它有三個用途：

1. 後續的分頁 query 用 `runId` 鎖定同一次執行的結果，避免重跑時讀到前後不一致的資料。
2. Workpaper 匯出時用 `runId` 確認自己匯出的到底是哪一次的規則執行。
3. 結果落地到 `result_*` 系列資料表時，`runId` 是 partition key。
