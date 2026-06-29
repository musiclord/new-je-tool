# SQL Server 2022 schema-per-project 遷移設計

> 狀態：設計定稿待使用者複審（brainstorming 產出，未進實作）
> 範圍：子系統 **A**（SQL Server provider 物理模型：DB-per-project → schema-per-project）＋ **B**（連線設定抽象層，中度）
> 日期：2026-06-25
> 對應報告：「JET 資料存取層遷移評估報告（v2）」§4–§11 與階段 0／階段三／階段四；本設計修正報告中與現況程式碼不符之處（見 §0）。

---

## 0. 背景與對報告的修正

### 0.1 現況事實（已對程式碼查證）

- **本機 SQLite**：一專案一個 `jet.db` 檔，位於 `{root}/{projectId}/`，root 預設 `AppContext.BaseDirectory\projects`。資料表不帶 `project_id` 欄（檔案即 scope）。見 `JetProjectFolder`、`JetProjectDatabase`。
- **雙 Provider 已落地**：約 25 組 repository 契約，每組有 `Sqlite* / SqlServer* / ProviderRouting*` 三件套；`IGlRepository` 等介面在 `Domain/RuleRepositories.cs`；provider 於專案建立時選定、寫入 `project.json` 的 `databaseProvider`、建立後不可變，由 `ProjectProviderResolver`（以 projectId 為鍵快取）解析。**provider 不走全域 DI 切換。**
- **現行 SQL Server 物理模型 ＝ DB-per-project**：`SqlServerProjectDatabase` 對每個專案 `CREATE DATABASE JET_{projectId}` / `DROP DATABASE`，所有表建在該庫的 `dbo` 下。庫名 `JET_{sanitize(projectId)}`，淨化為英數底線；不同案件名淨化後可能撞名，靠 create 時 `DatabaseExistsAsync` 擋。
- **連線設定（B 現況）**：無 `appsettings`／`IConfiguration`（WinForms 桌面程式、手寫 composition root）。SQL Server base 連線字串來自環境變數 `JET_SQLSERVER_CONNECTION`，由 `AppCompositionRoot` 注入，provider 以 `InitialCatalog` 切到 `JET_{projectId}`。無啟動健康檢查。
- **SQL Server 測試**：`SqlServerFact`/`SqlServerTheory`／`TempSqlServerProject.ProbeConnectionStringAsync` 以同一條 `JET_SQLSERVER_CONNECTION`（缺省 `(localdb)\MSSQLLocalDB`）探測；探測不到即 skip。
- **projectId 的真實字元集**：projectId ＝案件名稱＝資料夾名，允許 Unicode 文字／數字／空白／`_ - ( )`，最長 100 字（`ProjectNameRules`）。**不是 GUID。**

### 0.2 對報告的修正（本設計採用的版本）

| 報告原述 | 修正 |
|---|---|
| schema 名 ＝ `prj_` + 8 個隨機英數，存於 `dbo.Projects.SchemaName` | 改為**確定性衍生**：`prj_` + 淨化(projectId) + `_` + 8 hex 雜湊尾。零儲存、總函式、Unicode 安全、碰撞機率極低（8 hex ≈ 2⁻³²），最終由 schema 存在性檢查 backstop。 |
| provider 由設定檔／環境全域切換（本機 SQLite、正式 SQL Server） | provider 是 **per-project、寫在 project.json、不可變**；本設計不改此模型。 |
| 組態來自 legacy 全域變數，需搬到 dbo.ProjectConfig | 現況組態已存在每專案 DB 表（`config_field_mapping`、`config_filter_scenario`）；本範圍**不**動組態集中化（屬後續子專案）。 |
| §4 表名 `GL_raw`/`GL`/`TB_raw`/`W1..W10`… | 真實表名為 `staging_gl_raw_row`/`target_gl_entry`/`result_filter_run`…；`W1~W10`／`V/R/A` 代號已退役（AGENTS.md 鐵律），不得出現於表名/UI/新文件。 |

### 0.3 前提

- **Greenfield**：測試環境 SQL Server 2022 上無「不能丟」的既有 `JET_{projectId}` 資料，可直接以 schema-per-project 取代 DB-per-project，**無需資料搬遷路徑**。
- 測試環境為單機開發（localhost、Integrated Security、登入身分 `beep-boop\rich2`、sysadmin）。多人並發、鎖、稽核屬報告 §14／子系統 C，不在本範圍。

---

## 1. 目標與非目標

### 1.1 目標

1. 將 SQL Server provider 從 DB-per-project 改為**單一資料庫內 schema-per-project**，跑在測試環境的 SQL Server 2022。
2. 所有 `SqlServer*` repo 的裸表名 SQL 透過**單一收斂點**安全地綁定到專案 schema（方案 C）。
3. 連線設定改為 **appsettings 分層 + `SqlConnectionStringBuilder` 組合 + 啟動健康檢查**（中度），同時保住既有以 `JET_SQLSERVER_CONNECTION` 為基礎的測試。
4. 維持 jet-guide §13 的 **provider 等價**：SQLite 與 SQL Server 同 fixture 同結果。

### 1.2 非目標（明確不做，列為後續子專案）

SQLite provider 與資料夾/registry 身分模型不動；dbo 中央目錄的鎖/稽核/權限（C）、AD（D）、schema migration runner（E）、歸檔（F）、多人並發（報告 §14）皆不在本範圍。

---

## 2. 身分模型（維持 2026-06-22 現況，不變更）

本設計**不改動**專案身分模型，沿用 2026-06-22「案件名稱資料夾命名」設計（已實作、750 測試綠）：

- `projectId` ＝ **案件名稱**（caseName）：唯一、可讀、即資料夾名、建立後不可改名。
- `projectCode` ＝ **案件編號**（獨立欄位，非唯一鍵）；`entityName` ＝ 客戶名稱。
- 因 `projectId` 本身已是可讀案件名稱，SQL Server 端「schema 反查是哪件案子」由 `dbo.project_schema_map(schema_name, project_id)` 直接達成——`project_id` 欄即可讀名稱。**不新增任何顯示欄**（早期草案的 `caseName` 欄與 `map.case_name` 欄源自設計誤解，已移除；見 Task 5）。
- **schema 解析為純函式 `SqlServerProjectSchema.For(projectId)`，不查表**；`dbo.project_schema_map` 僅在建立/刪除專案時寫入/刪除，與查詢熱路徑無關，供 SSMS／admin 反查與防撞 backstop。

---

## 3. schema 命名

```
sanitized  = lower( 只保留 projectId 的 [A-Za-z0-9] )      // 中文名可能為空字串，允許
hash8      = lower_hex( SHA256(UTF8(projectId)) )[..8]      // 8 hex，穩定、防撞
schemaName = "prj_" + truncate(sanitized, 40) + "_" + hash8 // 總長 ≤ 64
白名單regex = ^prj_[a-z0-9]*_[0-9a-f]{8}$
```

- 範例：`光寶2025` → `prj_2025_<8hex>`；`A-1024` → `prj_a1024_<8hex>`；純中文 `合併案` → `prj__<8hex>`。
- 不同 projectId 即使淨化後相同（`A-B` vs `AB`），hash8 不同 → **碰撞機率極低**（8 hex ＝ 32-bit 雜湊尾，~2⁻³²）；create 時的 schema 存在性檢查為最終 backstop。
- 實作為純函式 `SqlServerProjectSchema.For(projectId) -> string`，並提供 `IsValid(schemaName) -> bool`（白名單）。

---

## 4. Token 收斂點（方案 C）

### 4.1 機制

- `SqlServerProjectDatabase` 新增命令工廠：
  `SqlCommand CreateCommand(SqlConnection conn, string projectId, string sqlWithTokens)`。
  它在**唯一一處**：(1) 以 `SqlServerProjectSchema.For(projectId)` 取得 schema；(2) 過白名單 `IsValid`，不合法即拋例外；(3) 以 `QUOTENAME` 等義的方括號包裹，把哨兵 `{s}` 全部替換為 `[prj_xxx]`；(4) 回傳設好 `CommandText` 的 `SqlCommand`。
- 所有發出資料表 SQL 的 `SqlServer*` repo（約 20+ 個，**確切清單於實作計畫逐一列舉**）：裸表名 `FROM import_batch` → `FROM {s}.import_batch`，所有 `connection.CreateCommand()` 改走工廠。
- DDL（§5）與 `SqlServerGlRepository` 現有的 `dbo.` 兩處 → 改用 `{s}.`。
- `SqlServerImportRepository` 的 `SqlBulkCopy.DestinationTableName`（非 SQL 文字，token 不適用）→ 直接用 `SqlServerProjectSchema.For(projectId)` 組 `[prj_xxx].{stagingTable}`。

### 4.2 安全論證

- `{s}` 不是合法 T-SQL 片段、不會自然出現在 SQL 字面值中，替換不會誤傷。
- schema 名注入防護（白名單 + 方括號）集中於收斂點唯一一處，符合報告 §11／§13「從第一行 repo 就守住」。

---

## 5. 建立／刪除專案（DDL，取代 CREATE/DROP DATABASE）

### 5.1 EnsureSingleDatabase（冪等，全系統一次）

- `IF DB_ID(N'{db}') IS NULL CREATE DATABASE [{db}]`（`{db}` 來自設定 `Sql:Database`，dev 缺省 `JET_DEV`）。測試環境 sysadmin 可建；生產偵測已存在即略過（DBA 預建）。
- 確保 `dbo.project_schema_map` 存在（`IF OBJECT_ID ... CREATE TABLE`）。

### 5.2 建立專案（單一 transaction）

1. `CREATE SCHEMA [prj_xxx]`
2. 在該 schema 內建所有專案表（DDL 用 `{s}.` token，內容對齊現行 `SchemaSql` 第 5 版形狀）
3. `INSERT dbo.project_schema_map (schema_name, project_id)`

CREATE SCHEMA 可進交易，任一步失敗整批 rollback（比舊的 CREATE DATABASE 不可進交易、原子性更好）。

### 5.3 刪除專案

1. DROP 該 schema 內所有表（SQL Server 要求 schema 清空才能 DROP SCHEMA）
2. `DROP SCHEMA [prj_xxx]`
3. `DELETE dbo.project_schema_map WHERE schema_name = @s`

取代舊的 `DROP DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE`。

### 5.4 連線

`CreateConnection(projectId)` 的 `InitialCatalog` 對所有專案都＝單庫；projectId 只用來算 schema 餵命令工廠。`EnsureCreatedAsync` 維持目前的「惰性、冪等」呼叫模式，但對象由「庫」變「schema＋表」。

---

## 6. 連線設定抽象層（B，中度）

### 6.1 設定檔

- `appsettings.json`（基底，進原始碼庫）：`Sql:Encrypt=true`、`Sql:ConnectTimeoutSeconds`、`Sql:ApplicationName="JET"`。
- `appsettings.Development.json`（進原始碼庫）：`Sql:Server=localhost`、`Sql:Database=JET_DEV`、`Sql:IntegratedSecurity=true`、`Sql:TrustServerCertificate=true`、`Sql:ApplicationName="JET-Dev"`。
- `appsettings.Production.json`（不進庫，部署注入）：`Server=<FQDN>`、`Database=JET`、`TrustServerCertificate=false`、`ApplicationName="JET-Prod"`。
- WinForms 以 `Microsoft.Extensions.Configuration` 手動建 `IConfiguration`；連線字串不直接寫設定檔，而由 `SqlConnectionStringBuilder` 在執行期從 `Sql:*` 參數組合。

### 6.2 來源優先序（保住既有測試）

1. 環境變數 `JET_SQLSERVER_CONNECTION` 若有 → 直接用（`SqlServerFact`／`TempSqlServerProject` 靠它，不可破）。
2. 否則由 appsettings `Sql:*` 經 `SqlConnectionStringBuilder` 組合。

環境選擇用 `JET_ENVIRONMENT`（缺省 `Production`；本機 dev 設 `Development`）。

> **部署 note — 單庫名（`SingleDatabaseName`）的來源。** 上面的優先序只決定 **base 連線字串**；**單庫名是另一條獨立來源**，只取自 `Sql:Database`（經 `AppCompositionRoot` 的 `config["Sql:Database"] ?? "JET_DEV"`），**不會**從 `JET_SQLSERVER_CONNECTION` 連線字串的 `InitialCatalog` 回填。因此生產部署除了提供伺服器連線，**必須**另行設定 `Sql:Database`（例 `appsettings.Production.json` 的 `"Sql":{"Database":"JET"}`）；若只設 `JET_SQLSERVER_CONNECTION` 而未設 `Sql:Database`，單庫名會退回預設 `JET_DEV`，使所有專案 schema 落在非預期的 `JET_DEV` 庫。

### 6.3 啟動健康檢查

- 若 SQL Server 已設定（步驟 6.2 取得到連線字串），開連線跑 `SELECT @@VERSION, DB_NAME(), SUSER_SNAME()`，結果寫進啟動日誌。
- 失敗時拋出含「目標伺服器 + 資料庫 + 認證模式（**不含密碼**）」的明確例外訊息，寫日誌。
- **不硬擋整個 app**：provider 是 per-project，純 SQLite 使用者不應被 SQL Server 連不上卡死；錯誤顯著呈現即可。選 sqlServer 的專案在連線時本就會得到明確錯誤（沿用現行 `sql_server_not_configured` 模式）。

---

## 7. 測試與驗證

- `TempSqlServerProject` 由「建/刪 DB」改為「建/刪 schema」；每個測試後清掉 `prj_*` schema 與對應 `dbo.project_schema_map` 列，確保隔離。
- jet-guide §13 **provider 等價測試**（SQLite vs SQL Server 同 fixture 同結果）為主驗收標準，全部需綠（指向 2022 後 `SqlServerFact` 自動接上）。
- 新增單元測試：
  - `SqlServerProjectSchema.For` 的確定性、白名單、淨化、防撞（不同 projectId → 不同 schema）。
  - 命令工廠 `{s}` 替換正確；輸入非法 projectId / 非法 schema 時拒絕並拋例外。
- 連線設定：`SqlConnectionStringBuilder` 組合的單元測試；`JET_SQLSERVER_CONNECTION` 優先序測試；健康檢查失敗訊息不含密碼的測試。

---

## 8. 檔案影響圖

**會動：**
- `Infrastructure/SqlServerProjectDatabase.cs`：單庫 + EnsureSingleDatabase + CREATE/DROP SCHEMA + 命令工廠 + DDL 改 `{s}.`。
- 新 `Infrastructure/SqlServerProjectSchema.cs`：純函式 `For` / `IsValid`。
- 所有發出資料表 SQL 的 `Infrastructure/SqlServer*.cs` repo（約 20+ 個，確切清單於計畫列舉）：裸名 → `{s}.`、改走命令工廠。
- `Infrastructure/SqlServerImportRepository.cs`：BulkCopy 目的表用 schema helper。
- `AppCompositionRoot.cs`：接 `IConfiguration`、連線來源優先序、啟動健康檢查接點。
- 新 `appsettings.json` / `appsettings.Development.json`（＋ Production 範本，不進庫）。
- 測試輔助 `TempSqlServerProject` 及相關 SQL Server 測試。

（身分模型不變更，故**不**動 `Domain/ProjectDocument.cs` 與 `ProjectCreateHandler`。）

**不動（後續子專案）：** SQLite provider、資料夾/registry 身分模型、dbo 鎖/稽核/權限/Config（C）、AD（D）、migration runner（E）、歸檔（F）、多人並發（報告 §14）。

---

## 9. 已知風險與緩解

| 風險 | 緩解 |
|---|---|
| schema 名注入 | 收斂點唯一處白名單 `^prj_[a-z0-9]*_[0-9a-f]{8}$` + 方括號包裹；schema 由系統衍生、不接受使用者輸入。 |
| 改 appsettings 破壞既有 SQL Server 測試 | `JET_SQLSERVER_CONNECTION` 優先序保住測試探測路徑。 |
| DROP SCHEMA 前未清表會失敗 | 刪除流程先 drop 所有表再 DROP SCHEMA。 |
| schema 解析查表拖慢熱路徑 | 解析為純函式；map 表僅 create/delete 用。 |
| 健康檢查硬擋誤殺純 SQLite 使用者 | 健康檢查不硬擋；僅顯著呈現錯誤。 |
| 大批匯入寫爆 transaction log | 沿用現行 `SqlBulkCopy` 串流 + `CommandTimeout=0` 路徑（jet-guide §13 已具備），本設計不退化。 |

---

## 10. 驗收標準

1. 指向測試環境 SQL Server 2022 後，選 `sqlServer` provider 的專案能完成 import → mapping → validate → prescreen → filter → export 全流程。
2. 建立專案在單庫內產生 `prj_xxx` schema 與預期所有表，並寫入 `dbo.project_schema_map`；刪除專案後 schema 與 map 列消失，單庫保留。
3. jet-guide §13 provider 等價測試全綠。
4. 故意給錯誤伺服器名時，啟動日誌清楚指出「以哪個身分連到哪個伺服器哪個資料庫被拒」，不含密碼，且純 SQLite 專案仍可運作。
