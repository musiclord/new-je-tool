# 單庫加固 + Express 退場 + 四層子資料夾化 實作計畫

> **For agentic workers:** 逐任務實作，每步用 checkbox（`- [ ]`）追蹤。每個任務以「驗收點（不 commit）」收尾——版控由使用者驗證後自行執行。規則類程式一律先寫紅燈測試再實作（jet-testing）。

**Goal:** 三條獨立工作流——(一) 把單庫資料隔離做成自動守衛、(二) SQL Server Express 退場並以 SQL Server 2022 為主要引擎、(三) 四層依既有分組拆進子資料夾。

**Tech Stack:** .NET 10 / C#、`Microsoft.Data.SqlClient`、xUnit、`NetArchTest.Rules`（已在測試專案）、原生 ADO.NET。

**對應 spec:** `docs/superpowers/specs/2026-07-01-single-db-hardening-and-layer-foldering-design.md`

## Global Constraints

- 依賴方向 `Bridge/Host → Application → Domain ← Infrastructure`；由 `LayerDependencyTests` 機器守；provider 分支只在 Infrastructure。
- 規則名稱用 `jet-guide.md` §4 登錄表名；V/R/A、W1~W10 流水代號已退役，不得出現於新程式/文件。
- 事實表讀寫走原生 SQL；不得把完整列集載入 Application 記憶體；不得用 EF Core。
- schema 名只能由 `SqlServerProjectSchema.For` 衍生、過白名單、方括號包；使用者輸入永不拼進識別字。
- 契約先行：動 action/payload/錯誤碼前先改 `docs/action-contract-manifest.md`。
- 子資料夾化一律**只搬檔、不改 namespace、不改 `using`**（namespace 維持單層）。
- **agent 全程不 commit、不 push、不提議 commit**；每任務以驗收點收尾。
- 每個守衛型測試都要證明「有牙」：以正向對照或臨時假違規確認會轉紅，再還原。

---

## 工作流一：單庫資料隔離守衛

### Task 1：原始碼靜態守衛（無裸專案表名，不需資料庫）

**Files:**
- Create: `src/JET/tests/JET.Tests/Architecture/SchemaIsolationGuardTests.cs`

**Interfaces:**
- Consumes: `JET.Domain.JetSchemaCatalog.All`（表名 oracle）
- Produces: 一個純 `[Fact]`，掃描 SQL Server 可達原始碼的 SQL 字面值，斷言無裸專案表名

- [ ] **Step 1: 寫測試（含正向對照，證明有牙）**
  - 掃描對象：`Infrastructure/Persistence/SqlServer/**/*.cs` + 共用述詞層 `Infrastructure/Sql/{GlRulePredicates,GlFilterWhereBuilder,ValidationSql,InfSamplePageSql,NullRecordsCategoryPredicate}.cs`（由測試組件位置向上尋 `JET.slnx` 定位 repo 根）。
  - 對 `JetSchemaCatalog.All` 每個 `PhysicalName`，找 SQL 字串字面值中每次出現，檢查緊鄰前綴 ∈ `{ "{s}.", "{schemaPrefix}", "{prefix}", "].", "].[" }`；否則記為違規（檔:行:表名）。略過 `//`、`///` 註解行。
  - **正向對照**：另加一段 in-test 的假 SQL 字串（`"SELECT * FROM target_gl_entry"`）餵給同一個掃描函式，`Assert` 它被判為違規——證明掃描邏輯真的會抓。
  - 主斷言：對真實原始碼掃描，違規集合為空（`Assert.Empty`，失敗訊息逐條列 檔:行:表名）。
  - 例外允許清單：以顯式具名常數承載（目前預期空集合）。
- [ ] **Step 2: 跑測試** — `dotnet test … --filter SchemaIsolationGuardTests`。Expected: 正向對照通過、主斷言通過（現況裸表名為 0）。若主斷言紅，代表真有漏網，逐條修 SqlServer repo 或述詞層。
- [ ] **Step 3: 驗收點（不 commit）**

---

### Task 2：雙專案行為守衛（真實引擎，跨專案零汙染）

**Files:**
- Create: `src/JET/tests/JET.Tests/Infrastructure/SchemaIsolationJourneyTests.cs`

**Interfaces:**
- Consumes: `SqlServerProjectDatabase`（schema 建/刪）、既有測試 harness（`TempSqlServerProject` / `HandlerTestHost`）
- Produces: `[SqlServerFact]`，同一單庫建兩專案、灌哨兵 fixture、驗 A 讀不到 B（對稱驗 B 讀不到 A）

- [ ] **Step 1: 寫測試**
  - 同一單庫建立專案 A 與 B（唯一 projectId → 唯一 schema）。A 的最小 fixture 全帶哨兵（如 `account_code` 前綴 `AAA`、`created_by` 前綴 `AAA`），B 全帶 `BBB`。
  - 對 A 跑每一條讀取路徑：`query.completenessDiffPage`/`docBalancePage`/`nullRecordsPage`/`infSamplePage`/`filterHitsPage`、`query.tagMatrix*`、`query.dataPreview`、`filter.preview`、`validate.run`、`prescreen.run`、匯出查詢。
  - 斷言（值＋身分雙鎖）：A 的每個結果集**只含 AAA、絕不含 BBB**；A 的計數＝只數 A 的 recount。對 B 對稱再驗一次。
  - `finally` 一律 `DeleteAsync` 兩專案（DROP SCHEMA + 刪 map 列），不殘留。
- [ ] **Step 2: 設定本機 `JET_SQLSERVER_CONNECTION` 指向 SQL Server 2022 單庫，跑測試** — Expected: 通過（無汙染）。若紅＝真有隔離漏洞，回頭修對應 repo 的 schema 限定。
- [ ] **Step 3: 驗收點（不 commit）**

---

## 工作流二：SQL Server Express 退場 → SQL Server 2022

### Task 3：Express 提示強化（第 1 級；純呈現，無 wire 變更）

**Files:**
- Modify: `src/JET/JET/Application/SystemDatabaseInfoHandler.cs`（`Summarize` 的 Express 分支文案）

**Interfaces:**
- Consumes: `SqlServerBackendInfo.IsExpress`（已存在）
- Produces: 偵測到 Express 時，摘要句明講「單庫模型會撞 10 GB 上限，建議改用 SQL Server 2022」

- [ ] **Step 1: 改文案**：`IsExpress` 為真時，於現有 ⚠ 摘要後補上單庫 10 GB 上限與「建議 SQL Server 2022」一句。純後端組字、`isExpress` 欄位不動 → **無 manifest 變更**。
- [ ] **Step 2: 補/改 Application 測試**：`SystemDatabaseInfoHandler` 對 Express fixture 斷言摘要含新提示字樣（值鎖，不鎖整句）。
- [ ] **Step 3: build + test** — Expected: 綠。
- [ ] **Step 4: 驗收點（不 commit）**

---

### Task 4：開發/測試遷移到 SQL Server 2022（第 2 級）

**Files:**
- Modify: `src/JET/tests/JET.Tests/Infrastructure/SqlServerFact.cs`（探測與閘控）
- Modify: `TempSqlServerProject`（所在檔；探測目標）

**Interfaces:**
- Produces: 探測預設目標為 SQL Server 2022 執行個體；`JET_SQLSERVER_CONNECTION` 覆寫保留；新增「非 Express 且版本 ≥ 2022」前置判定，不符即以具名理由 skip

- [ ] **Step 1: 探測目標與版本判定**：`SqlServerAvailability` 探測時，除連得上外，另讀 `SERVERPROPERTY('EngineEdition')` / `ProductMajorVersion`，判定「非 Express（EngineEdition ≠ 4）且主版本 ≥ 16（2022）」。不符 → `IsAvailable=false`、`SkipReason` 改為「偵測到的 SQL Server 非 2022 或為 Express，略過」。
- [ ] **Step 2: 文件化本機需求**：在計畫「後續」與 `development-status.md` 註明開發需 SQL Server 2022 Developer；`JET_SQLSERVER_CONNECTION` 指向它。
- [ ] **Step 3: build + 跑全套件**（本機連 2022）— Expected: `SqlServerFact` 不再 skip、provider 等價測試實跑。
- [ ] **Step 4: 驗收點（不 commit）**

> **後續（不在本計畫實作，隨上線推進）——第 3、4 級：** 第 3 級 `project.create` 選 sqlServer 且連 Express 時回**新錯誤碼 `sql_server_express_unsupported`**（**先改 `action-contract-manifest.md`**，再改 `ProjectCreateHandler`）；第 4 級所有 sqlServer 操作連 Express 一律以該碼擋下。實作前另立紅燈驗收測試。

---

### Task 5：跨 provider 排序 parity 修復（收斂到 2022 後釘死排序；可獨立切出）

**Files:**
- Modify: `src/JET/JET/Infrastructure/Persistence/SqlDialect.cs`（`ISqlDialect` 加姓名排序 collation 片段）
- Modify: `Infrastructure/Persistence/{Sqlite,SqlServer}/…CreatorSummaryExportRepository.cs`（及其他以姓名排序的匯出查詢）

**Interfaces:**
- Produces: `string ISqlDialect.NameOrderingCollation`（SQLite 回 `""`；SQL Server 回 `" COLLATE Latin1_General_BIN2"`），套在「依姓名排序」的 `ORDER BY` 之後

- [ ] **Step 1: 確認現況紅**：跑 `ProviderParityJourneyTests.CreatorSummaryExport_FullList_IsEquivalentAcrossProviders`，留紅燈輸出為證。
- [ ] **Step 2: 加方言片段並套用**：`ISqlDialect` 加 `NameOrderingCollation`（OCP：擴充不改壞既有）；把姓名排序的 `ORDER BY <count> DESC, <name>` 改為 `ORDER BY <count> DESC, <name>{dialect.NameOrderingCollation}`。理由：`Latin1_General_BIN2` 對 BMP 中文字＝碼位序，與 SQLite 預設位元序一致。
- [ ] **Step 3: 跑測試轉綠**：該 parity 測試綠；跑全套件確認 0 紅。
- [ ] **Step 4: 驗收點（不 commit）**

---

## 工作流三：四層子資料夾化（只搬檔、namespace 不變）

### Task 6：`.editorconfig` 收斂到專案根

**Files:**
- Create: `src/JET/JET/.editorconfig`
- Delete: `src/JET/JET/Infrastructure/.editorconfig`

- [ ] **Step 1**：於專案根建 `.editorconfig`，`[*.cs]` 下 `dotnet_style_namespace_match_folder = false` + `dotnet_diagnostic.IDE0130.severity = none`（一次涵蓋四層），移除 Infrastructure 內那份（DRY）。
- [ ] **Step 2: build** — Expected: 0 警告 0 錯誤。
- [ ] **Step 3: 驗收點（不 commit）**

---

### Task 7：Application 子資料夾化（44 檔）

**Files:** 全部 `git mv`（namespace 不動）。逐檔歸屬：

```text
Ports/        IApplicationActionHandler, IHostShell, IProjectSession, IJetEventPublisher, IDemoFileWriter, ISqlServerBackendProbe
Contracts/    SqlServerBackendInfo, TabularSourcePayload
Support/      PayloadReader, FilterScenarioPayloadParser, FilterRunMaterializeService, DemoDataFactory
Handlers/Import/  ImportAccountMappingHandler, ImportAuthorizedPreparerFromFileHandler, ImportCalendarHandlers,
                  ImportFromFileHandler, ImportInspectFileHandler, ImportPreviewFileHandler
Handlers/Query/   QueryCompletenessDiffPageHandler, QueryDataPreviewHandler, QueryDocBalancePageHandler,
                  QueryFilterHitsPageHandler, QueryInfSamplePageHandler, QueryNullRecordsPageHandler,
                  QueryTagMatrixRowPageHandler, QueryTagMatrixScenariosHandler, QueryTagMatrixVoucherPageHandler
Handlers/Host/    HostExitAppHandler, HostOpenFolderHandler, HostSelectFileHandler, HostSelectFilesHandler, HostSelectSavePathHandler
Handlers/ (根)    DemoHandlers, DevDbHandlers, DevLogHandlers, ExportWorkpaperStreamHandler, FilterHandlers,
                  MappingHandlers, MessageLogHandlers, PrescreenRunHandler, ProjectHandlers,
                  SystemDatabaseInfoHandler, SystemPingHandler, ValidateRunHandler
```

- [ ] **Step 1**：`mkdir` 上述子資料夾；逐組 `git mv`（每組後 `dotnet build` 一次抓錯）。
- [ ] **Step 2: build** — Expected: 0 警告 0 錯誤（namespace 未變，預期逐檔中立）。
- [ ] **Step 3: 驗收點（不 commit）**

---

### Task 8：Domain 子資料夾化（54 檔）

**Files:** 全部 `git mv`（namespace 不動）。逐檔歸屬：

```text
Abstractions/  RuleRepositories, IProjectExportLocator, IWorkpaperWriter,
               ICompletenessAccountPageRepository, ICompletenessDiffPageRepository, IDocBalancePageRepository,
               IFilterHitsPageRepository, IInfSamplePageRepository, INullRecordsPageRepository,
               ITagMatrixRowPageRepository, ITagMatrixScenariosRepository, ITagMatrixVoucherPageRepository
Contracts/     AccountMappingContracts, AuthorizedPreparerContracts, CalendarContracts, DiagnosticLogContracts,
               FilterContracts, ImportContracts, MessageLogContracts, PrescreenContracts, RuleRunContracts,
               ValidationContracts, WorkpaperContracts, WorkpaperReferenceContracts, DataPreview, PageRows, DevDatabaseInspection
Rules/         PrescreenRules, RuleCatalog, GlProjectionGuard, GlRowProjector, TbRowProjector, MappingValidator,
               MappingSuggestionEngine, MappingSpecs, FilterScenario, QuarterEndWindows, GlCanonicalNames, GlFieldWhitelist
Primitives/    MoneyScaling, Paging, DateNormalizer, NonWorkingDays, DatasetKind, GlAmountMode, TbChangeMode,
               ProjectNameRules, TabularHeaderNormalizer, CsvDialectDetector
Domain/ (根)   JetActionException, JetJsonStorage, JetSchemaCatalog, ProjectDocument, DiagnosticNdjson
```

- [ ] **Step 1**：`mkdir` 子資料夾；逐組 `git mv`（每組後 `dotnet build`）。
- [ ] **Step 2: 確認靜態守衛不受影響**：Task 1 掃的是 Infrastructure，Domain 搬移不影響；但確認 `JetSchemaCatalog` 仍可由 `JET.Domain` 存取（namespace 未變 → 是）。
- [ ] **Step 3: build** — Expected: 0 警告 0 錯誤。
- [ ] **Step 4: 驗收點（不 commit）**

---

### Task 9：Bridge 拆分 —— 使用者已裁定「不執行」（2026-07-01）

Bridge 4 檔在 §14 門檻內，維持扁平。本任務**不執行**（保留紀錄：若日後 Bridge 檔案增長越過門檻，再依 `Transport/` + `Dispatch/` 切分）。

---

### Task 10：文件更新（隨程式碼同批）

**Files:**
- Modify: `AGENTS.md`（File Map：四層新子資料夾結構）
- Modify: `docs/jet-guide.md` §13（Express 退場、目標 2022）
- Modify: `docs/development-status.md`（provider 策略：2022 為主、開發需 Developer 版；隔離守衛已落地）
- Modify: `src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerProjectDatabase.cs` 類別註解（移除「Express 與 Standard 共用」語氣，改為目標 2022）
- Append: `docs/development-log.md`（2026-07-01 條目：本輪三工作流、做了什麼、驗證、未 commit）

- [ ] **Step 1**：逐檔更新如上（狀態用「已落地，待 GUI 驗收」措辭；不誇大）。
- [ ] **Step 2: 驗收點（不 commit）**

---

### Task 11：全套件回歸與驗收（驗證任務）

- [ ] **Step 1: build** — `dotnet build src/JET/JET.slnx --no-restore --nologo`，Expected: 0 警告 0 錯誤。
- [ ] **Step 2: 全套件（連 SQL Server 2022）** — `dotnet test …`，Expected: 全綠、0 skip（含 Task 1 靜態守衛、Task 2 雙專案隔離、Task 5 排序 parity 轉綠、`LayerDependencyTests` 仍綠）。
- [ ] **Step 3: 突變思維自查（jet-testing §6）**：對 Task 1 掃描函式與 Task 5 方言片段各問「改一個字元會不會轉紅」；不會就補斷言。
- [ ] **Step 4: 驗收點（不 commit）** — 回報做了什麼、驗證了什麼、跳過什麼；commit 留給使用者。

---

## Self-Review（計畫對 spec 的覆蓋自查）

- **spec §2 隔離守衛** → Task 1（靜態，oracle＝JetSchemaCatalog）、Task 2（雙專案行為，[SqlServerFact]）。
- **spec §3 Express 退場** → Task 3（第 1 級提示）、Task 4（第 2 級 dev/test 遷移 + 非-Express 閘控）、Task 5（排序 parity）；第 3、4 級列 Task 4 後續段（契約先行）。
- **spec §4 四層子資料夾化** → Task 6（.editorconfig 收斂）、Task 7（Application）、Task 8（Domain）、Task 9（Bridge，可選預設略過）。
- **spec §5 測試策略** → Task 1/2 守衛、Task 3/4/5 二的測試、Task 11 回歸。
- **spec §3.4/§4.3 文件** → Task 10。
- **型別一致性**：`ISqlDialect.NameOrderingCollation`、`JetSchemaCatalog.All`、`SchemaIsolationGuardTests`/`SchemaIsolationJourneyTests` 命名在各任務一致。
- **未定案決策點**（見交付訊息的提問）：Bridge 拆或不拆、Task 5 是否併本批、Express 第 3/4 級是否本輪就做、metadata 集中化是否確認延後。
