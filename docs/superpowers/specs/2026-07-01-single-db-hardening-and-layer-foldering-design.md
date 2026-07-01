# 單庫資料隔離守衛 + SQL Server Express 退場 + 四層子資料夾化 設計

> 狀態：設計定稿，待使用者複審與實作（尚未動任何程式）。
> 範圍：三條互相獨立的工作流——(一) 單庫資料隔離的自動守衛、(二) SQL Server Express 退場並以 SQL Server 2022 Developer/Standard 為「SQL Server」選項的主要引擎、(三) 把 Bridge/Application/Domain/Infrastructure 四層依既有分組拆進子資料夾。
> 對應實作計畫：`docs/superpowers/plans/2026-07-01-single-db-hardening-and-layer-foldering.md`
> 承接：`docs/superpowers/specs/2026-06-25-sqlserver-schema-per-project-design.md`（單庫 schema-per-project 已落地，本設計是它的加固與延伸）。

---

## 0. 背景與現況事實（已對程式碼查證，2026-07-01）

- **SQL Server 已是單庫 schema-per-project**：所有專案共用一個資料庫（開發 `JET_DEV`、生產預計 `JET`），每個專案一個 `prj_<淨化>_<8hex>` schema（`SqlServerProjectSchema.For`，純函式、白名單 `^prj_[a-z0-9]*_[0-9a-f]{8}$`）。事實表建在該 schema 內、不帶 `project_id` 欄；`dbo.project_schema_map(schema_name, project_id)` 為唯一的跨專案反查表。
- **schema 限定的唯一收斂點**：`SqlServerProjectDatabase.CreateCommand(connection, projectId, sqlWithTokens)` 把哨兵 `{s}` 替換為過白名單的 `[prj_xxx]`；共用述詞層 `GlRulePredicates` / `GlFilterWhereBuilder` / `ValidationSql` / `InfSamplePageSql` 則收 `schemaPrefix` 參數（SQLite 傳 `""`、SQL Server 傳 `SqlServerProjectSchema.QualifierFor(projectId)` ＝ `[prj_xxx].`）。裸表名掃描目前為 0 筆（見前輪分析），但**沒有任何自動測試把這件事釘住**。
- **隔離只靠 schema 牆**：單庫模型下，一句漏掉限定詞的 SQL（`FROM target_gl_entry` 而非 `FROM {s}.target_gl_entry`）就會讓 A 專案讀到 B 專案的資料。這是單庫模型最嚴重的正確性／資安風險，目前是人工守。
- **Express 與 2022 目前共用實作**：`SqlServerProjectDatabase` 的類別註解明載「SQL Server Express（開發）與 Standard/Enterprise（生產）共用本實作，差異僅在連線字串」。開發與測試用 LocalDB（`(localdb)\MSSQLLocalDB`，Express 系）——`SqlServerFact`／`TempSqlServerProject.ProbeConnectionStringAsync` 以 `JET_SQLSERVER_CONNECTION`（缺省 LocalDB）探測。`SqlServerHealthCheck` 已偵測 `IsExpress` 並在 `system.databaseInfo` 以 ⚠ 呈現。
- **專案 metadata 仍是檔案式**：`ProjectDocument`（案件名、期間、provider…）持久化為 `projects/{projectId}/project.json`，SQL Server 專案也是。中央目錄 `dbo.project_schema_map` 只記 schema↔專案兩欄。（把 metadata/組態集中進單庫是**另一條後續工作**，不在本設計範圍，但本設計的隔離守衛與 Express 退場是它的前置地基。）
- **中央表名登錄**：`Domain/JetSchemaCatalog.All` 宣告式列出專案 DB 的每一張實體表（`PhysicalName`），是全庫表名的單一事實來源。
- **四層目前的檔案量**：Bridge 4、Application 44、Domain 54、Infrastructure 已於 2026-07-01 前一輪拆入子資料夾。四層 namespace 一律單層（`JET.Bridge` / `JET.Application` / `JET.Domain` / `JET.Infrastructure`）。
- **依賴方向已有機器守衛**：`tests/JET.Tests/Architecture/LayerDependencyTests.cs`（NetArchTest 掃 IL）鎖住 Application/Bridge 不得依賴 Infrastructure、Domain 不得依賴外層。
- **一個既有的跨 provider parity 紅燈**：`ProviderParityJourneyTests.CreatorSummaryExport_FullList_IsEquivalentAcrossProviders` 失敗——同編製筆數（2326）的中文姓名，tie-break 排序在 SQLite（位元序）與 SQL Server（資料庫 collation）不一致。此紅燈早於本設計，屬本設計工作流二順帶處理的對象。

---

## 1. 目標與非目標

### 1.1 目標

1. **把「單庫資料隔離」從人工守升級為機器守**：任一 SQL Server 路徑若引用了未經 schema 限定的專案表，測試即轉紅；並以真實雙專案（two-tenant）行為測試證明 A 專案永遠讀不到 B 專案的資料。
2. **SQL Server Express 漸進退場**：把「SQL Server」選項的主要引擎正式定為 SQL Server 2022（開發用 Developer 版、生產用 Standard/Enterprise），開發與測試環境由 LocalDB 遷移至 SQL Server 2022，並把 Express 從「⚠ 提示」逐級升為「不支援」。
3. **四層依既有分組拆進子資料夾**：Application 與 Domain 依審計工作流程 / 職責分組拆入子資料夾；namespace 維持單層不變（沿用 Infrastructure 已落地的作法）；Bridge 依 §14 門檻判定是否需拆。
4. 全程遵守 SOLID / Clean Code / harness 工程紀律：TDD 紅→綠、每步可自跑 build/test、依賴方向與契約不破、agent 不 commit。

### 1.2a 已確認的設計決策（使用者裁定 2026-07-01）

- **Bridge 維持扁平、不拆**：4 檔在 §14 門檻內，避免過度結構化（Clean Code）。計畫 Task 9 確認不執行。
- **排序 parity 併入本批**：`CreatorSummaryExport` 的 collation 紅燈於本 spec 工作流二 Task 5 一併修復，達成全套件 0 紅。
- **Express 退場本輪只做第 1、2 級**：提示強化 + 開發/測試遷移到 2022；第 3 級（`sql_server_express_unsupported` 硬擋建案）與第 4 級隨上線推進，另立紅燈驗收後再做（契約先行）。
- **metadata / 組態的單庫集中化延後**：本 spec 只鋪地基（隔離守衛 + Express 退場），`project.json → dbo 目錄`集中化與 schema 層權限列為**下一份獨立 spec**。

### 1.2 非目標（明確不做，列為後續）

- **不**把 metadata / 組態集中進單庫（`project.json` → `dbo` 目錄）——那是下一條工作流，本設計只鋪地基。
- **不**動 SQLite / DuckDB 的檔案式身分模型（一案一檔，維持不變）。
- **不**做多人並發鎖、稽核、schema 層權限 / AD、歸檔——屬更後段。
- **不**改 wire 契約的既有形狀（除工作流二可能新增一個錯誤碼，契約先行處理）。
- **不**改任何規則語意、SQL 述詞或金額/日期計算。

---

## 2. 工作流一：單庫資料隔離守衛（紅線）

### 2.1 要釘住的不變量

> 在 SQL Server 單庫模型下，**任何會被 SQL Server 執行的專案表引用，都必須經過 schema 限定**（`{s}.` 命令工廠或 `schemaPrefix` 參數）；裸表名一律禁止。SQLite 路徑（一案一檔）不受此約束。

這條不變量有兩個面向要各自守：**原始碼面**（沒有任何 SQL 字面值出現裸專案表名）與**行為面**（實際跑起來，A 專案的查詢永遠不含 B 專案的資料）。兩者互補，缺一不可——原始碼守衛快、隨時可跑、但看不到動態組出來的 SQL 是否正確；行為守衛慢、需真實引擎、但直接證明隔離成立。

### 2.2 守衛一之一：原始碼靜態守衛（隨時可跑，不需資料庫）

一個純 `[Fact]`（無 I/O、毫秒級），掃描「會被 SQL Server 執行到」的原始碼，斷言其中的 SQL 字面值不含裸專案表名。

- **oracle（單一事實來源）**：`Domain/JetSchemaCatalog.All` 的 `PhysicalName` 集合，就是「所有專案表」的權威清單。守衛直接取用它，**不另抄一份表名**（DRY；日後新增表只改登錄表一處，守衛自動涵蓋）。
- **掃描對象**：`Infrastructure/Persistence/SqlServer/**` 全部 `.cs`，加上被兩個 provider 共用、以 `schemaPrefix` 參數限定的述詞層檔案（`GlRulePredicates`、`GlFilterWhereBuilder`、`ValidationSql`、`InfSamplePageSql`、`NullRecordsCategoryPredicate`、`InfSamplePageSql`）。**不掃** `Sqlite/**`（裸表名在一案一檔下是正確的）。
- **判定**：對每個表名，找出它在 SQL 字面值中的每一次出現，檢查緊鄰其前的限定詞是否屬於允許集合 `{ "{s}.", "{schemaPrefix}", "{prefix}", "].", "].[" }`；任一裸出現即失敗，訊息指出「檔案:行:表名」。只檢查字串字面值內容，略過 `//` 與 `///` 註解（表名常出現在說明文字裡，不算違規）。
- **例外機制**：若確有極少數合理例外（目前預期為零），以一份**顯式、具名、附理由**的允許清單承載，不得靜默略過——符合 harness「沒有隱形放行」原則。
- **定位 repo 根**：由測試組件位置向上尋 `JET.slnx` 標記，避免綁死機器路徑（可重複性，jet-testing §2）。

**SOLID/Clean 對應**：SRP（守衛只做一件事）、DRY（表名清單重用 `JetSchemaCatalog`）、OCP（新增表不需改守衛）。

### 2.3 守衛一之二：雙專案行為守衛（真實引擎，[SqlServerFact] 閘控）

一個 `[SqlServerFact]`（偵測不到 SQL Server 即 skip）：在**同一個單庫**內建立**兩個**專案 schema，各灌入帶有專案專屬哨兵值的最小固定 fixture（幾列即可，例如 A 專案的 `account_code`/`created_by` 全帶前綴 `AAA`、B 專案全帶 `BBB`），然後對 A 專案跑過每一條讀取路徑（各 `query.*Page`、filter preview、validate、prescreen、tag matrix、data preview、匯出查詢），斷言：

- A 專案的結果**只**含 A 的哨兵值，**絕不**出現任何 B 的哨兵值（值＋身分雙鎖，jet-testing §6）；
- A 的計數等於「只數 A」的 recount；
- 對 B 對稱再驗一次（B 讀不到 A）。

這是真正的「紅線」——它不管 SQL 是怎麼組出來的，直接證明隔離在真實引擎上成立。它與既有 `ProviderParityJourneyTests`（單專案、比 SQLite↔SQL Server 等價）**不重複**：那些測等價，這條測隔離，是新的等價類（避免 jet-testing 殺蟲劑悖論）。

fixture 刻意極小（FIRST/Fast）；每個測試用唯一 projectId → 唯一 schema，結束一律 `DROP SCHEMA` + 刪 map 列，不殘留（jet-testing §2 Independent、§Mystery guest）。

### 2.4 兩道守衛的分工

| 守衛 | 需要 SQL Server | 速度 | 抓什麼 | 角色 |
|:---|:---|:---|:---|:---|
| 原始碼靜態守衛 | 否 | 毫秒 | 裸表名（在它變成漏洞之前，於 build/test 時擋下） | 隨時可跑的第一道紅線 |
| 雙專案行為守衛 | 是（[SqlServerFact]） | 秒級 | 實際跨專案汙染 | 真實引擎上的最終證明 |

CI 無 SQL Server 時，行為守衛 skip、但靜態守衛仍在，紅線不會整條消失。

---

## 3. 工作流二：SQL Server Express 退場 → SQL Server 2022 Developer/Standard

### 3.1 為什麼一定要退場（這是硬限制，非偏好）

單庫模型把「所有專案的資料」放進同一個資料庫。SQL Server Express 有**單一資料庫 10 GB 上限**；而實測單一百萬列案件的會計資料就接近 1 GB。於是 10 GB 從「每案各自的額度」變成「全部案件的總天花板」，十幾個大案即撐爆、整個系統寫不進去。因此 **Express 與「單庫管所有案件」在設計上互斥**。目標引擎改為無此上限的 SQL Server 2022（Developer 版免費、功能與生產同級；生產用 Standard/Enterprise）。

### 3.2 漸進退場階梯（先做前兩級，後兩級隨上線推進）

分四級，逐級收緊，避免一次硬剎車：

1. **提示強化（本輪）**：`system.databaseInfo` 偵測到 Express 時，摘要句明講「單庫模型會撞 10 GB 上限，建議改用 SQL Server 2022」。這是純呈現（後端組字），`isExpress` 欄位已在契約內，**無 wire 形狀變更**。
2. **開發/測試遷移（本輪）**：把 `SqlServerFact`／`TempSqlServerProject` 的預設探測目標由 LocalDB 改為 SQL Server 2022 執行個體；`JET_SQLSERVER_CONNECTION` 覆寫路徑不變（既有測試不破）。並在 parity 套件加一道前置斷言：探測到的引擎**不是 Express**、且版本 ≥ SQL Server 2022；否則以清楚理由 skip（誠實顯示「沒測到 2022」，不靜默通過）。開發測試因此與生產同引擎、同 collation、無容量落差。
3. **擋建案（後續）**：`project.create` 選 sqlServer 且連到的引擎是 Express 時，回新錯誤碼 `sql_server_express_unsupported`（契約先行：先改 `action-contract-manifest.md`），阻止在 Express 上建立新專案。
4. **全面不支援（後續）**：所有 sqlServer 操作連到 Express 一律以同一錯誤碼明確擋下。

本設計**實作第 1、2 級**；第 3、4 級寫入計畫的「後續」段，隨 SQL Server 正式上線推進。

### 3.3 順帶解決：跨 provider 排序 parity 紅燈（可獨立切出）

既有紅燈 `CreatorSummaryExport_FullList_IsEquivalentAcrossProviders` 的根因是 tie-break 排序的 collation 差異。既然主要引擎收斂到 SQL Server 2022，就能把排序釘死為**跨 provider 一致**：在「依姓名排序」的匯出查詢，SQL Server 端對姓名鍵加一個位元序 collation（`COLLATE Latin1_General_BIN2`，對 BMP 內的中文字＝碼位序，與 SQLite 預設的位元序一致），SQLite 端不加。此差異片段收進既有的方言縫（`ISqlDialect`，OCP：擴充而非改壞），呼叫端只多帶一個「姓名排序 collation」的方言片段。修完該 parity 測試轉綠，全套件 0 紅。此項與 Express 退場相關但可獨立切出，計畫中列為單獨任務。

### 3.4 契約與文件影響

- **契約**：第 1、2 級無 wire 變更；第 3 級新增錯誤碼 `sql_server_express_unsupported`（先改 manifest）。
- **現況規格**：`jet-guide.md` §13 provider 策略段（把「Express 與 Standard/Enterprise 共用、差異僅連線字串」改寫為「目標引擎為 SQL Server 2022；Express 逐步退場、單庫模型下不支援」）、`SqlServerProjectDatabase` 類別註解、`development-status.md` provider 策略段一併更新（文件隨程式碼同一變更打包）。

---

## 4. 工作流三：Bridge / Application / Domain / Infrastructure 四層子資料夾化

### 4.1 原則（沿用 Infrastructure 已落地的作法）

- **只做物理搬移，namespace 一律維持單層**（`JET.Application` / `JET.Domain` / `JET.Bridge`）——與 Infrastructure 前一輪一致，也與本專案「資料夾只做人為分類、不驅動 namespace」的慣例一致（jet-guide §14「資料夾不強制 Clean Core」）。SDK 自動 glob + namespace 於檔內宣告，故**零 `using`／零契約變更、build 逐檔中立**。
- 以 `git mv` 搬移（git 認列為 rename，歷史保留）。
- `.editorconfig` 收斂：把目前 Infrastructure 專屬的那份提升為 `src/JET/JET/.editorconfig`（專案根，關 IDE0130 一次涵蓋四層），移除 Infrastructure 內那份，避免重複（DRY）。
- 搬移後 `LayerDependencyTests`（以 namespace 界層，與資料夾無關）必然仍綠——這正是它價值所在：重構結構、邊界不破。

### 4.2 各層 taxonomy

**Bridge（4 檔）——建議維持扁平，不拆。** 依 §14「同層檔案超過 3–5 個或已出現明確群組才拆」，4 個高內聚檔（`ActionDispatcher`、`DispatcherDiagnostics`、`JetWebMessageBridge`、`WebViewEventPublisher`）在門檻內；為 4 個檔硬造子資料夾是過度結構化（Clean Code：沒有收益就不加結構）。**若仍要拆**，唯一合理切法是 `Transport/`（`JetWebMessageBridge`、`WebViewEventPublisher`）+ `Dispatch/`（`ActionDispatcher`、`DispatcherDiagnostics`）——計畫中列為可選、預設不執行，交使用者裁定。

**Application（44 檔）——四個頂層分組：**

| 子資料夾 | 內容（職責） |
|:---|:---|
| `Ports/` | Application 對外宣告的埠介面：`IApplicationActionHandler`、`IHostShell`、`IProjectSession`、`IJetEventPublisher`、`IDemoFileWriter`、`ISqlServerBackendProbe` |
| `Contracts/` | Application 層 DTO：`SqlServerBackendInfo`、`TabularSourcePayload` |
| `Support/` | handler 共用的無狀態輔助：`PayloadReader`、`FilterScenarioPayloadParser`、`FilterRunMaterializeService`、`DemoDataFactory` |
| `Handlers/` | 全部 action handler；其下再依審計工作流程分組，凡一組達數檔者立子資料夾（`Import/` 6、`Query/` 9、`Host/` 5），其餘（Project/Mapping/Filter/Export/Rules/System/Dev/Demo/Messaging，多為 1–2 檔）留在 `Handlers/` 根 |

**Domain（54 檔）——四個頂層分組：**

| 子資料夾 | 內容（職責） |
|:---|:---|
| `Abstractions/` | 倉儲 / 埠介面：`RuleRepositories`、九個 `I*PageRepository`/`ITagMatrixScenariosRepository`、`IProjectExportLocator`、`IWorkpaperWriter` |
| `Contracts/` | 各 `*Contracts.cs`、`DataPreview`、`PageRows`、`DevDatabaseInspection`（純資料形狀） |
| `Rules/` | 規則規格與投影/驗證邏輯：`PrescreenRules`、`RuleCatalog`、`GlProjectionGuard`、`GlRowProjector`、`TbRowProjector`、`MappingValidator`、`MappingSuggestionEngine`、`MappingSpecs`、`FilterScenario`、`QuarterEndWindows`、`GlCanonicalNames`、`GlFieldWhitelist` |
| `Primitives/` | 值物件 / 列舉 / 純函式：`MoneyScaling`、`Paging`、`DateNormalizer`、`NonWorkingDays`、`DatasetKind`、`GlAmountMode`、`TbChangeMode`、`ProjectNameRules`、`TabularHeaderNormalizer`、`CsvDialectDetector` |

`Domain/` 根保留跨層共用的少數基石：`JetActionException`、`JetJsonStorage`、`JetSchemaCatalog`、`ProjectDocument`、`DiagnosticNdjson`（AGENTS.md 明訂跨層共用契約放 Domain 根，維持易尋）。

精確的逐檔歸屬表列於實作計畫（避免本設計與計畫兩份事實漂移）。

### 4.3 文件影響

`AGENTS.md` 的 File Map 段更新四層的新子資料夾結構；namespace 不變故其餘現況規格不受影響。

---

## 5. 測試策略（jet-testing 對齊）

| 工作流 | 層級 | 形式 | oracle / 斷言 |
|:---|:---|:---|:---|
| 一 靜態守衛 | Infrastructure（純 `[Fact]`） | 原始碼掃描 | oracle＝`JetSchemaCatalog.All`；斷言「無裸專案表名」，違規列 檔:行:表名 |
| 一 行為守衛 | Application 或 Infrastructure（`[SqlServerFact]`） | 雙專案汙染測試 | 哨兵值身分鎖（A 結果不含 B 值）＋ recount 計數 |
| 二 引擎閘控 | Infrastructure（`[SqlServerFact]` 前置斷言） | 版本/版別探測 | 非 Express 且 ≥ 2022，否則具名 skip |
| 二 排序 parity | 既有 `ProviderParityJourneyTests` | golden/differential | 修 collation 後 `CreatorSummaryExport` 轉綠、全套件 0 紅 |
| 三 子資料夾化 | 不新增測試 | build + 既有全套件 | build 0 警告 0 錯誤；`LayerDependencyTests` 仍綠；全套件回歸綠 |

harness 紀律：每個實作任務先寫會紅的測試、跑確認紅、實作、跑確認綠、留驗收點（不 commit）。守衛型測試以「先引入一個假違規→確認轉紅→還原」證明有牙（或以正向對照，比照 `LayerDependencyTests`）。

---

## 6. 風險與取捨（顯式陳述）

- **靜態守衛的偽陽/偽陰**：只掃字串字面值、略過註解，仍可能被極端字串攔法騙過（偽陰）或誤判（偽陽）。緩解：以雙專案行為守衛作真值兜底；例外走顯式具名允許清單、不靜默。此為「快而粗」與「慢而準」互補，非單點依賴。
- **開發/測試遷移到 2022 的環境成本**：開發機需裝 SQL Server 2022 Developer（免費）。緩解：`JET_SQLSERVER_CONNECTION` 覆寫保留；無 2022 時 parity 測試具名 skip、不誤綠。
- **collation 片段的可攜性**：`Latin1_General_BIN2` 是 SQL Server 專屬；DuckDB 未來加入時需各自決定其位元序 collation。緩解：收進 `ISqlDialect` 方言縫，加 provider＝加一片段，不改呼叫端（OCP）。
- **四層搬移的量體**：Application 44 + Domain 54 檔的 `git mv`。緩解：namespace 不變＝零程式變更；逐組搬、每組後 build；git 認列 rename。風險等同前一輪 Infrastructure（已驗證中立）。
- **Bridge 不拆的判斷**：可能與「四層都拆」的字面期待有落差。取捨：遵守專案自己的 §14 門檻與 Clean Code，勝過形式一致；已在 §4.2 顯式說明並保留可選拆法交裁定。

---

## 7. 檔案影響圖（總覽；逐檔步驟見計畫）

- **工作流一**：新增 `tests/JET.Tests/Architecture/SchemaIsolationGuardTests.cs`（靜態）與 `tests/JET.Tests/Infrastructure/`（或 Application）下的雙專案行為測試；讀取 `Domain/JetSchemaCatalog`。零生產碼變更。
- **工作流二**：改 `Infrastructure/…/SqlServer/SqlServerHealthCheck.cs`（Express 摘要句）、`Application/SystemDatabaseInfoHandler.cs`（摘要透傳，如需）、`tests/…/SqlServerFact.cs` 與 `TempSqlServerProject`（探測目標、非-Express 前置斷言）、排序方言片段（`ISqlDialect` + 用到的匯出 repo）；文件 `jet-guide.md`§13、`development-status.md`、`SqlServerProjectDatabase` 註解。第 3 級另加 `action-contract-manifest.md` 錯誤碼與 `ProjectCreateHandler`（後續）。
- **工作流三**：四層 `git mv` 進子資料夾；`.editorconfig` 收斂至 `src/JET/JET/.editorconfig`；`AGENTS.md` File Map。零 namespace / 契約變更。

---

## 8. 落地順序建議

先做**工作流一（隔離守衛）**——它風險最低、價值最高，且直接鎖住單庫模型最該守的紅線；接著**工作流二第 1、2 級 + 排序 parity**；最後**工作流三（子資料夾化）**——純結構、放最後以免與前兩者的檔案改動互相干擾。三條彼此獨立，也可分批交付、分批由使用者驗證。
