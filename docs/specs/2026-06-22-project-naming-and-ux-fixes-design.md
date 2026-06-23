# 設計：案件名稱資料夾命名 + 套用測試案件不跳步 + 授權編製人員預覽

> 狀態：已實作並通過測試（750 個案例通過，含 SQL Server parity；final Opus 複審結論 READY-TO-MERGE）。第二項「不跳步」屬純前端，仍待使用者在 GUI 上人工驗收。
> 日期：2026-06-22
> 範圍：三項收尾修改，全部遵守 JET 的架構鐵律（契約先行、前端零商業邏輯、business logic 不外移、provider 分支只在 Infrastructure、雙 provider 等價）。

## 背景與目標

使用者實機操作後提出三項收尾修改：

1. **案件名稱資料夾命名**：`projects/` 底下的專案資料夾目前以雜湊命名（32 個十六進位字元的 GUID），很難辨識是哪個案件。所以建立案件流程要新增一個「案件名稱」欄位，並讓專案資料夾以案件名稱命名。
2. **「套用測試案件」不自動跳步**：點擊後只填當前區塊的資料，不自動跳到下一個流程。例如在「建立案件」按下套用後，應停在建立案件，不要跳到「匯入資料」。
3. **授權編製人員清單預覽**：「匯入資料」區塊除了既有的科目配對預覽外，再新增一個授權編製人員清單的預覽。

## 全域約束（每個 task 隱含適用）

- **契約先行**：凡是動到 action 的 wire shape，先改 `docs/action-contract-manifest.md` 再實作；由 `ActionContractTests` 鎖住回應形狀。
- **前端零商業邏輯**：HTML/JS 不組 SQL、不做驗證或計算；`postMessage` 只在 `jet-api.js`。
- **provider 分支只在 Infrastructure**：Application 與 Domain 不得出現 sqlite/sqlServer 的條件分支；新查詢一律寫三份實作（Sqlite、SqlServer、ProviderRouting），並配 `[SqlServerFact]` 的 parity 測試。
- **TDD**：規則類或驗證類，先寫紅燈測試再實作。
- **雙 provider 等價**：任何新增查詢在兩種引擎下結果要一致。
- **既有專案零遷移**：`projects/` 內既有的雜湊資料夾必須照常開啟（使用者明確要求）。

---

## Finding 1：案件名稱作為資料夾名

### 使用者已拍板的三項決策

| 決策 | 選擇 |
|:---|:---|
| 命名來源 | **新增「案件名稱」欄位**(與案件編號、客戶名稱並列) |
| 唯一性策略 | **資料夾完全等於案件名稱**(建立時擋非法字元 + 拒絕重複) |
| 既有專案 | **維持原狀、照常開啟**(只有新案件用可讀名稱) |

### 核心模型：`projectId` 就是案件名稱

目前的 `projectId`（32 個十六進位字元的 GUID）一身三任：它同時是 DB 鍵、是資料夾名（`{root}/{projectId}`）、也是 SQL Server 庫名的來源（`JET_{淨化後的 projectId}`）。此外，`IsValidProjectId`（規則 `^[0-9a-f]{32}$`）還兼任 path-traversal（路徑穿越）的守衛。

在「資料夾完全等於名稱」這個決策下，最小、也最不易出錯的做法，是讓 `projectId` 直接等於案件名稱，藉此維持既有不變式「資料夾名 == projectId == DB 鍵」。

> 被否決的替代方案：保留 GUID 當 `projectId`、資料夾另用案件名稱、再存一份 name 對 id 的對照。這會把 `JetProjectFolder` 從純函式（`GetProjectDirectory(id) = root/id`）改成需要掃描查找的東西，牽動 `GetProjectDirectory`、`GetDatabasePath`、`GetProjectJsonPath`、`EnumerateProjectIds` 全部，複雜度和出錯面都遠高於本方案。

**取捨（使用者已接受）**：案件名稱即 id，會帶來兩個後果——建立後不能改名（目前本來就沒有改名功能），而且名稱必須唯一。這正是「完全等於名稱 + 拒絕重複」的直接結果。

### 案件名稱驗證規則（建立時與 id 守衛共用）

定義一個單一驗證器，取代 `JetProjectFolder.IsValidProjectId` 原本的 32-hex 規則：

- **長度**：去前後空白後，1–100 字元。
- **允許字元**：Unicode 文字（`\p{L}`，含中日韓）、數字（`\p{N}`）、空白、`_`、`-`、半形 `()`、全形 `（）`。
- **拒絕字元**：`/ \ : * ? " < > |`，以及 `.`（句點）與控制字元。禁用 `.` 同時達到兩個目的：天然杜絕 `..` 路徑穿越，也避開「結尾句點」這個 Windows 陷阱。
- **拒絕**：前導或結尾空白；以及 Windows 保留名（不分大小寫）`CON`、`PRN`、`AUX`、`NUL`、`COM1`–`COM9`、`LPT1`–`LPT9`。
- **空白**：`null` 或全空白視為無效。

**為什麼遷移是免費的**：上述允許的字元集是舊 32-hex GUID 的超集——hex 字元都是英數、長度 32 不超過 100、也不會撞到保留名。因此既有專案的 `projectId` 仍然有效，`EnumerateProjectIds` 仍會列出它們，`GetProjectDirectory` 也仍放行，舊雜湊專案照常開啟，零遷移。

**安全性**：`GetProjectDirectory` 仍是先驗證、再 `Path.Combine(RootPath, projectId)`；因為拒絕了 `. / \ :`，所以 id 無法形成相對上溯或絕對路徑。這是本項最敏感之處，配上完整的正向與負向單元測試。

### 唯一性把關落在哪裡

`JsonFileProjectStore.CreateAsync` 是以 `Directory.CreateDirectory`（同名時靜默覆寫）加 `WriteAsync` 實作，本身不擋重複。因此唯一性檢查放在 `ProjectCreateHandler`（Application 層，與 provider 無關）：建立前先用 `projectStore.FindAsync(caseName)` 查詢，一旦命中就丟 `JetActionException`（訊息：「案件名稱『X』已存在，請換一個。」）。

### 契約變更（契約先行）

`project.create` 的 payload 新增一個選填欄位 `caseName`（string）。提供時，它經驗證與唯一性檢查後，作為 `projectId` 與資料夾名；未提供時，回退為 `Guid.NewGuid().ToString("N")`，維持既有的程式化或測試建立行為（既有測試免改）。UI 的建立表單則強制必填（前端 `required`），所以使用者路徑一律會有可讀名稱。回應仍為 `{ projectId, ok }`。先改 `docs/action-contract-manifest.md`，`ActionContractTests` 同步。

> 設計取捨：選填加 GUID 回退是刻意的決定。`project.create` 的 payload 在大約 15 處測試裡各自內嵌組裝（沒有中央 helper）；若強制必填，會 churn 全部這些測試卻沒有使用者效益（測試專案用 GUID 資料夾並無妨）。改用選填，就把「必填」這件事落在真正重要的 UI 層，契約也保持向後相容。

`ProjectCreateHandler.HandleAsync` 的處理：
- 讀選填的 `caseName`：有值時 `Trim`、跑驗證器（失敗丟 `InvalidPayload`）、做唯一性檢查（見上），再以它作為 `ProjectId`；無值時 `ProjectId = Guid.NewGuid().ToString("N")`。其餘欄位不變。

`ProjectDocument` 結構不變（不新增欄位）：由 `ProjectId` 承載案件名稱。UI 把 `projectId` 以「案件名稱」標籤呈現；`projectCode`（案件編號）維持為獨立欄位。

### SQL Server 庫名

`SqlServerProjectDatabase.DatabaseName` 沿用現有的 `JET_{只保留英數底線}` derivation，**不改**，以免既有 SQL Server 專案的 `JET_{guid}` 庫對不上。CJK 字元在 `char.IsLetterOrDigit` 為真，會被保留下來；庫名以 `[...]` 內嵌，本身已安全。

**極端情境的防護（具體機制）**：兩個不同的案件名稱淨化後有可能撞到同一個庫名。例如「A 公司」和「A公司」，因為淨化會去掉空白，兩者都會變成 `JET_A公司`。若不處理，第二案會沉默地共用第一案的 SQL Server 庫，造成資料汙染。防護做法是在 `IProjectDatabaseInitializer` 新增 `Task<bool> DatabaseExistsAsync(string projectId, CancellationToken)`：SQL Server 查 `DB_ID(N'JET_{...}') IS NOT NULL`，SQLite 查 `File.Exists(jet.db)`（由 routing 委派）。`ProjectCreateHandler` 在 `FindAsync` 唯一性檢查**之後**再加這道 gate：`DatabaseExistsAsync` 為真就擋下並提示換名。對 SQLite 而言，此時 DB 檔位於尚不存在的資料夾內，所以恆為 false，這道檢查只是無害的防禦縱深；對 SQL Server 則確實攔下淨化後的碰撞。

### Demo 專案命名

`DemoHandlers` 的 `project.loadDemo` 回傳的 metadata 新增 `caseName`，來源是 `DemoDataFactory` 新增的一個常數（例如 `DemoCaseName = "範例測試案件"`）。`DemoProjectData` 也增加 `CaseName` 欄位。

**已知後果（使用者已被告知）**：demo 案件名稱是固定的，所以重複點「套用測試案件（建立）」時，第二次會因同名被「已存在」擋下（這與一般使用者建立同名案件的行為一致）。要重跑就得先刪掉舊的 demo 專案。本設計刻意不對 demo 加唯一後綴，因為那會違反使用者選的「完全等於名稱」。

### Blast radius（實作時逐檔對齊，不是湊綠）

- `JetProjectFolder`：改寫 `IsValidProjectId` 規則；相關單元測試要涵蓋（接受合法案件名稱與舊 32-hex；拒絕 `..`、路徑分隔字元、保留名、空白、超長、前後空白）。
- `ProjectCreateHandler` 及其測試：payload 改用 `caseName`，並測唯一性與驗證的錯誤路徑。
- 既有測試呼叫 `project.create` 時**不提供 `caseName`**，因此回退到 GUID，行為與今天相同，**免改**（約 15 處內嵌 payload 不動）。`DemoProjectPipeline` 等 helper 同理（維持每測試隔離，避免在共用 instance 上撞到同名的 SQL Server 庫）。只**新增** caseName 的行為測試。
- `DemoDataFactory`、`DemoHandlers`、`DemoProjectPipeline`、前端 mock：demo 專案補上 `caseName`。

### Finding 1 測試矩陣

- 驗證器：合法案例（中文名、含空白／`-`／`_`／括號、舊 32-hex）；非法案例（`含/斜線`、`a..b`、`C:x`、空字串、`  前後空白 `、`CON`、101 字元、含 `*?"<>|`）。
- `project.create`：正常建立（projectId == caseName、資料夾名 == caseName）；缺 `caseName` 回 InvalidPayload；非法 caseName 回 InvalidPayload；同名重複回明確錯誤；sqlServer 同庫名衝突被擋下。
- 既有專案相容：以 32-hex id 建立的專案，`project.load`、`list`、`delete` 仍正常。
- 路徑安全：`GetProjectDirectory` 對含 `..` 或分隔字元的 id 丟例外。

---

## Finding 2：「套用測試案件」不自動跳步

### 根因

`create-step.js` 的 `mockCreateProject` 最後呼叫 `Ui.applyLoadedProject(loaded)`，而 `applyLoadedProject`（在 `ui-core.js`）會依 `data.project.currentStep` 設定步驟索引（新建專案的 currentStep = 1 = 匯入）。因此在建立案件時點「套用測試案件」，會被帶到「匯入資料」。

### 設計

- 讓 `applyLoadedProject(data, options)` 新增一個可選的 `options.stayOnCurrentStep`（預設 false，維持現行依 currentStep 導覽的行為）。當它為 true 時，照常套用所有狀態（專案、匯入態、配對、規則結果、情境），但不改變目前的步驟索引。
- `create-step.js` 的 `mockCreateProject` 改呼叫 `applyLoadedProject(loaded, { stayOnCurrentStep: true })`，套用後停在「建立案件」（顯示已建立摘要）。
- **不動**的部分：正式「建立案件」表單送出（第 116–123 行，仍導覽到匯入，因為這是正常建立後的前進）；以及從專案清單開啟舊案的 resume（第 310 行，仍 resume 到保存的步驟）。
- **逐一檢視其餘 demo loader**：`import`、`mapping`、`validate`、`filter` 的 mock loader 都不得自動前進。現況看來它們只做 `setState` 加 `addMessage`、不導覽（例如 `mockImportData` 並未呼叫 `gotoStep` 或 `applyLoadedProject`），但實作時要逐一確認；若有導覽就一併移除。

### 驗證

這是純前端 GUI 行為，依 jet-testing 的硬邊界，不做 WinForms 或 WebView2 的 E2E 自動化。改用 `/verify` 或請使用者手動確認：在各步點「套用測試案件」後，停在當前步、資料正確填入、不前進。

---

## Finding 3：授權編製人員清單預覽

這項鏡射既有的科目配對預覽（`accountMappings`）。授權名單存在 `target_authorized_preparer`（單欄 `name`）。

### 後端（契約先行 + 雙 provider）

- **契約**：`docs/action-contract-manifest.md` 的 `query.dataPreview` 允許的 dataset 新增 `authorizedPreparers`。
- **Domain**：`DataPreviewDataset` enum 加 `AuthorizedPreparers`；`DataPreviewDatasetNames.TryParse` 加 `"authorizedPreparers"` 分支；`QueryDataPreviewHandler` 的錯誤訊息允許值列表補上它。
- **Infrastructure**：`SqliteDataPreviewRepository` 與 `SqlServerDataPreviewRepository` 的 `dataset switch` 各加一支 `AuthorizedPreparers => await AuthorizedPreparersPreviewAsync(connection, limit, ct)`。查詢為 `SELECT name FROM target_authorized_preparer ORDER BY name`（SQLite 用 `LIMIT @limit`、SQL Server 用 `OFFSET 0 ROWS FETCH NEXT @limit`），另加 `COUNT` 取總數；回傳 `DataPreviewResult`（columns 為 `["preparerName"]`、rows 為 names、無 stats），鏡射 `AccountMappingsPreviewAsync`。`ProviderRoutingDataPreviewRepository` 無須改（已委派）。
- **column id**：回 `preparerName`，前端標籤對應「姓名」。

### 前端（零邏輯）

- `data-preview.js`：`DATASETS` 加 `{ value: 'authorizedPreparers', label: '授權編製人員清單' }`；`COLUMN_LABELS` 加 `preparerName: '姓名'`；`emptyHint` 加 `authorizedPreparers` 的文案（「尚未匯入授權編製人員清單…」）。
- `import-step.js`：授權編製人員卡片（以「已匯入」為顯示門檻）加一個「預覽」鈕 `data-action="preview-authorized-preparer"`，綁定 `Ui.openDataPreview('authorizedPreparers')`，鏡射既有的 `preview-account-mapping`。

### Finding 3 測試

- `QueryDataPreviewHandler` 與 Domain：`TryParse("authorizedPreparers")` 成功；handler 接受該 dataset。
- Infrastructure：匯入授權名單後，`GetPreviewAsync(AuthorizedPreparers)` 回正確的列數、總數、排序；空表回 totalCount 0。涵蓋 SQLite 與 SQL Server（`[SqlServerFact]` parity）。
- `ActionContractTests`：`query.dataPreview` 允許的 dataset 含 authorizedPreparers。

---

## 風險與待辦

- **最高風險：`IsValidProjectId` 的改寫**（它是安全閘）。以完整的負向測試覆蓋路徑穿越；字元集採白名單（預設拒絕），`.`、分隔字元、`:` 一律擋。
- **SQL Server 庫名淨化碰撞**：以「建立前檢查目標庫是否已存在」緩解（見 Finding 1）。
- **demo 重複建立**：固定的 demo 名稱重複套用會被擋下，這是「完全等於名稱」的預期後果，已告知使用者。
- 三項彼此獨立，可分 task 平行設計，但要同一輪實作、同一輪測試、同一次提交。
