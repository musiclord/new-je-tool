# JET UX 修正與資料表正規命名 — 設計提案

- 狀態：**全部實作完成、自動化全綠（1087 個案例通過，含 SQL Server LocalDB parity）、待 GUI 驗收**（2026-06-23 r2，經多代理 SDD 逐 task 複審 APPROVED）。第 7 項的範圍、以及第 3a 項的解鎖門檻，在實作前再經使用者拍板修正（見決策記錄）。**沒有實體改名任何資料表；尚未 commit**（版控待使用者驗收後再決定）。
- 日期：2026-06-23
- 範圍：七項分散問題的修正，全部圍繞一致的「以審計員為出發點」基準與既有架構邊界。
- 權威來源對照：canonical（正準）命名來自 `legacy/vba-1120/DbSchema.cls` 與 `legacy/vba-mvp/{AppSchema.bas,DbSchemaStaging.cls,DbSchemaTarget.cls}`；程序順序來自 `legacy/drawio/JE_diagram.drawio`；方法論來自 `legacy/jet-legacy-notes.md`；現行權威為 `docs/jet-guide.md`、`docs/action-contract-manifest.md`、`docs/jet-frontend-description.md`。

---

## 0. 決策記錄（使用者拍板）

1. **工具中文名**定為「分錄測試自動化工具」（英文 JE Testing Tool）。現行的「JET 傳票測試工具」是誤譯，須改。
2. **資料表正規命名的範圍**定為「中央三層登錄 + 呈現層正規名（不實體改名）」。這裡的「三層登錄」指：建立一個 Domain 型別 `JetSchemaCatalog` 當作唯一事實來源，登錄 19 張表，每張表附上正準審計名、所屬層級（Source / Staging / Target / System 四層中的一層）、以及面向（audience，分 DataView / StructureOnly / Hidden），並加上漂移守門。〔實作前修正：調查發現現行表名散落在 341 處 inline SQL、橫跨 73 個檔案，所以使用者裁定不做實體改名、不寫 migration，改用上述 `JetSchemaCatalog` 方案；資料預覽與文件以正規名呈現，並隱藏 staging 與 scratch 表。這樣零審計邏輯風險。〕下方第 6、7 節保留原案（實體改名）的敘述，但以本決策為最終準。
3. **科目配對的序位**定為「併入『資料驗證與測試』步驟的後段」，位置在驗證卡與預篩選卡之間，且「驗證跑過即解鎖」。〔實作前修正：解鎖門檻定為「`validate.run` 已執行」，完整性差異本身不擋——這是為了避免不重大的差異卡住下游的配對與篩選；也不為此新增獨立步驟。〕
4. （沿用既有決策）KCT 條件 B 仍屬 Phase 2，卡在 KCT 交付 BS/IS 分類表；面板上以 disabled 佔位。（KCT 是審計方法論裡的一組進階篩選條件，下文詳述。）

---

## 1. 工具命名修正（誤譯）

**現況**：有三處硬字串寫成「JET 傳票測試工具」——`Form1.cs:42`、`wwwroot/index.html:6`（`<title>`）、`wwwroot/index.html:30`（`<h1>`）。eyebrow 文字「Journal Entry Testing」（`index.html:29`）本來就是對的。

**設計**：三處統一改為「分錄測試自動化工具」；`<title>` 採樣板格式「分錄測試自動化工具 (JE Testing Tool)」。

**契約與風險**：純文案，沒有 wire 或契約影響，零審計邏輯。

**影響檔案**：`Form1.cs`、`wwwroot/index.html`。

---

## 2. KCT 條件：獨立且顯著的 A–J 面板 + 豁免情境名稱／動機

**現況**：KCT 目前是 `ui-core.js` 中 `FILTER_RULE_GROUPS` 的第 5 組，以 quick-add 按鈕混在其餘四組旁邊，不夠顯著（見 `filter-step.js` 的 `quickAddGroupsHtml` 與 `kctPresetButtonsHtml`）。情境名稱（`name`）與篩選動機（`rationale`）目前是前後端雙重必填：前端在 `filter-step.js:504-535`（`scenarioGate`），後端在 `Domain/FilterScenario.cs:113-121`。Phase 1 的型別（A、C、D、H、J）與預設（E、F、G、I）都已存在。

**設計**：
- 在進階篩選區塊頂部新增一個獨立的「KCT 小組條件」面板，與既有的自訂條件分區隔開（視覺上明確標示為方法論檢核清單），列出 A–J 十顆按鈕；其中 B 以 disabled 加「待 BS/IS」標示。按鈕點擊後即帶入對應的 AST 或預設，並進入該情境草稿。
- **KCT 來源的情境豁免** `name` 與 `rationale` 必填。判定方式有兩種：情境帶一個來源標記（例如 `source: "kct"`），或者「全部條件都屬 KCT 集合」時豁免。前後端一致放寬。
- KCT 條件的「動機」由方法論固定提供：後端可填入該條件的標準說明字串，維持留痕、不為空，因此不要求審計員逐條輸入。

**契約（contract-first，先改 manifest）**：`action-contract-manifest.md` 的 Filter 情境 schema 加上一個 KCT 來源標記欄；`invalid_scenario` 原本的「name/rationale 必填」條件改為「非 KCT 來源時必填」。

**架構邊界**：前端只負責組 AST 與呈現；名單、述詞、驗證仍留在後端。不得把任何 KCT 判斷邏輯寫進 JS。

**影響檔案**：`ui-core.js`、`steps/filter-step.js`、`Domain/FilterScenario.cs`、`Application/FilterScenarioPayloadParser.cs`（若新增來源欄）、`action-contract-manifest.md`、`docs/jet-frontend-description.md`。

---

## 3a. 科目配對序位：併入驗證步驟後段、完整性後解鎖

**現況**：科目配對的**檔案匯入**在 Step 1（`import-step.js:77-134` 的 `accountMappingCard`），它解鎖「未預期借貸組合」預篩選與「科目配對分析」篩選。完整性測試則在 Step 3（`validate-step.js`）。

**權威順序**（`JE_diagram.drawio:2099`）：先完成完整性測試，才進行借貸不平測試**及 Accounting Mapping**；完成借貸不平測試後，才進行可靠性測試。

**設計**（對齊使用者拍板的「併入驗證步驟後段」）：
- 把科目配對卡從 Step 1 移到 Step 3「資料驗證與測試」的**後段區塊**，位置在完整性測試結果之後。
- **在完整性測試通過之前（或已執行且差異已檢視之前），科目配對區塊維持鎖定**，並說明前置條件；通過後解鎖匯入、重匯入、以及「科目配對分析」相關條件。
- 後端的 gating 不能只靠前端：`unexpectedAccountPair` 預篩選、以及 `accountPair` 與新配對篩選的可用性，延續既有「需先匯入科目配對」的後端 N/A 守門（`PrescreenRunHandler`）。序位調整只改前端的呈現與解鎖時機，不鬆動後端的前置檢查。

**審計觀點**：在母體尚未證實完整之前，科目分類沒有意義，因為完整性是地基。

**契約與風險**：不改 wire（沿用既有的 import.accountMapping 與 validate/prescreen action）；純粹是前端步驟內的版面與解鎖時機重排。要保留 resume 情境（既有專案已匯入配對）時的正確呈現。

**影響檔案**：`steps/import-step.js`（移出配對卡）、`steps/validate-step.js`（納入後段配對區塊與完整性 gating）、`state.js`（若配對狀態的步驟歸屬需調整）、`docs/jet-frontend-description.md`。

## 3b. 進階篩選新增條件「考量特殊科目類別配對」

**現況**：既有的 `accountPair` 述詞（`GlRulePredicates.cs`，模式有 `exact`、`debitAnchor`、`creditAnchor`）語意是「錨定一邊、看對方」。

**設計**：新增一個**顯式雙類別加上否定**的配對條件，分三種模式：
1. **Dr A, Cr B**：同一張傳票同時存在「借方屬類別 A」與「貸方屬類別 B」。
2. **Dr A, Cr not B**：借方屬 A，且該傳票貸方**不含**類別 B。
3. **Dr not A, Cr B**：貸方屬 B，且該傳票借方**不含**類別 A。

借方欄位與貸方欄位由使用者各選一個標準化分類（白名單為 Revenue、Receivables、Cash、Receipt in advance、Others）。述詞以 `EXISTS`（正面）與 `NOT EXISTS`（否定）組合，參數化、純 ANSI、雙 provider 等價；借方側判定為 `amount_scaled >= 0`、貸方側為 `< 0`（與既有述詞一致）。

**契約（先改 manifest）**：Filter AST 新增一個 `type`（例如 `specialAccountCategoryPair`）與一個 `pairLogic`（三模式的列舉），加上 `debitCategory` 與 `creditCategory`；`invalid_scenario` 增列（類別不在白名單、需先匯入科目配對）。

**架構邊界**：Domain 放列舉與驗證；Infrastructure 放述詞與 WHERE builder；Application 放 parser。前端只組 AST。

**影響檔案**：`Domain/FilterScenario.cs`、`Infrastructure/GlRulePredicates.cs`、`Infrastructure/GlFilterWhereBuilder.cs`、`Application/FilterScenarioPayloadParser.cs`、`ui-core.js`、`steps/filter-step.js`、`action-contract-manifest.md`、`docs/jet-guide.md`（§6 條件列）。

---

## 4. 日期維度：可選擇一週七天裡哪幾天為非工作日（預設六、日）

**現況**：日期維度卡（`import-step.js:200-217`）目前只能上傳假日／補班檔。週末的判定是**寫死**在 `Infrastructure/SqlDialect.cs` 的 `WeekendPredicate`（SQLite 為 `strftime('%w') IN ('0','6')`；SQL Server 為 `DATEDIFF%7 IN (5,6)`）。正準的 `DATE_DIMENSION`（`AppSchema.bas:25-38`）裡，`IsWeekend` 註記為系統自動產生、`DayOfWeek` 範圍 1–7。

**設計**：
- 新增**每案的設定「非工作日是一週的哪幾天」**，預設為 `{6=Sat, 0=Sun}`（依正準的 1–7 對映後落定）。使用者完全沒操作時，就是預設的六、日。
- 日期維度卡新增**七天勾選控制**（週一到週日），把選擇寫入該設定。
- 後端把寫死的週末述詞**參數化**為讀取此設定；provider 分支仍留在 Infrastructure。週末與假日預篩選、以及（若有的話）非營業日的 KCT 條件 I，一律改讀此設定。

**契約**：由 `project.create` 或 `project.load`，或一個專屬的 calendar 設定 action，帶上 `nonWorkingDays`（一週幾的集合）；manifest 對應更新。缺欄時補上預設值，確保既有專案 resume 時行為不變（等同六、日）。

**審計觀點**：非營業日因地制宜（例如中東或特定地區）。KCT 條件 I（非營業日分錄）也明示：臺灣與中國大陸以外的地區需自行提供非營業日。

**影響檔案**：`steps/import-step.js`（七天勾選 UI）、`Domain`（設定型別與預設）、`Infrastructure/SqlDialect.cs`（參數化週末述詞）、相關 prescreen repository、`action-contract-manifest.md`、`docs/jet-guide.md`（§2.4 DateDimension 與 §5 週末規則）。

---

## 5. 建立案件：移除「產業別」與「金額 scale」顯示

**現況**：`create-step.js` 的表單有「產業別」（`:84` 輸入、`:62` 摘要、`:112` payload）；「金額」則指已建立摘要中的「金額 scale」（`:66`，顯示 `moneyScale` 這個內部縮放因子）。後端 `Domain/ProjectDocument.cs` 有 `Industry?`；`Application/ProjectHandlers.cs` 會讀 `industry`。

**設計**：
- **產業別**：整條移除（表單輸入、摘要列、payload、後端 `ProjectDocument.Industry`、handler 的讀取、manifest 的 `project.create` 與 DemoProjectDto）。它沒有任何規則或匯出在消費，移除安全。
- **金額 scale**：只從建立案件的**摘要顯示**移除這一列；`moneyScale` 這個後端概念保留，因為 amount_scaled = 金額 × MoneyScale 是精度地基，不可拿掉。

**契約**：`action-contract-manifest.md` 的 `project.create` 移除 `industry?`。

**影響檔案**：`steps/create-step.js`、`Domain/ProjectDocument.cs`、`Application/ProjectHandlers.cs`、`action-contract-manifest.md`、（若有的話）Demo 相關 DTO。

---

## 6 & 7. 資料表正規命名 + 資料預覽正規目錄

### 7. 中央表名登錄 + 實體改名（canonical audit 命名）

**現況**：實體 schema（v5）定義在 `Infrastructure/JetProjectDatabase.cs` 的單一 `SchemaSql` 字串（SQLite）與 `SqlServerProjectDatabase.cs`（SQL Server），表名為 `staging_*`、`target_*`、`result_*`、`config_*`，且這些名稱**散落在 40 多處 inline SQL、沒有中央常數**。這套命名繼承自上一代的 C# port（`legacy/JET-legacy/.../SchemaNames.cs`），並不是審計詞彙。

**設計**：
1. **建立中央表名登錄**（仿 legacy 的 `JetTable` enum 加 `ISchemaNames` resolver）：一個邏輯表身分的列舉，加一個 provider 解析器，所有 SQL 一律經登錄取名。這是改名安全的前提，也順手把散落的字串收斂成單一事實來源。
2. **實體改名**業務表為 canonical 名（見下表）。staging、中間、結果表維持內部命名、對使用者隱藏。
3. **既有資料庫遷移**：schema 由 v5 升到 v6，以 `ALTER TABLE ... RENAME TO`（兩種 provider 都支援）逐表改名；過程要冪等、單交易、版本回填。索引與外鍵參照一併更新。
4. **完整性與總覽類**（`COMPLETENESS_CALCULATED`、`COMPLETENESS_DIFF`、`COMPLETENESS_DETAIL`、`VALIDATION_OVERVIEW`、`ENGAGEMENT_OVERVIEW`、`DATA_OVERVIEW`）：維持用 CTE 即算（現行 `ValidationSql.cs`），透過預覽層以正準名呈現；至於是否落為實體表，列為日後議題。

**Canonical 對照（實體改名的目標）**：

| 現行實體表 | Canonical 名 | 角色 | 使用者可見 |
|---|---|---|---|
| `staging_gl_raw_row` | `JE_PBC` | 匯入原貌 GL(客戶提供) | 可見 |
| `staging_tb_raw_row` | `TB_PBC` | 匯入原貌 TB | 可見 |
| `target_gl_entry` | `JE` | 標準化分錄(測試母體) | 可見 |
| `target_tb_balance` | `TB` | 標準化試算表餘額 | 可見 |
| `target_account_mapping` | `ACCOUNT_MAPPING` | 科目→標準化分類 | 可見 |
| `target_authorized_preparer` | `AUTHORIZED_PREPARER` | 授權編製人員清單 | 可見 |
| `staging_calendar_raw_day`(+ 計算) | `DATE_DIMENSION`(呈現) | 日期維度 | 可見(呈現層) |
| `import_batch`/`_source`、`config_*`、`result_*`、`gl_control_total`、`app_message_log`、`schema_info` | (維持內部名) | 批次/組態/結果/控制/系統 | 隱藏 |
| (CTE) | `JE_IN_PERIOD`/`JE_NOT_IN_PERIOD`/`COMPLETENESS_*`/`*_OVERVIEW` | 期間切分/完整性/總覽 | 呈現層(暫不落表) |

> 待確認細節（實作時與使用者校準）：`JE` 與 `JE_IN_PERIOD` 之間的對應關係。目前 `target_gl_entry` 即標準化母體，期間切分是查詢層的 `period_in_out`，並不另外落表。

**審計一致性保證（硬性）**：改名是**純結構重命名**，不得改變任何查詢的語意。守法方式：
- 全套件 993 個測試須維持全綠；
- 對 demo 案件跑 validate、prescreen、filter，**改名前後逐欄、逐命中身分都相同**（golden-master 差異測試，對齊 `jet-testing` SKILL 第 5 節）；
- 維持 SQLite 與 SQL Server LocalDB 的雙 provider parity。

**影響檔案**：`Infrastructure/JetProjectDatabase.cs`、`SqlServerProjectDatabase.cs`、所有 repository、predicate、validation SQL（經登錄改寫）、新增的表名登錄型別、`docs/jet-guide.md`（§2 資料模型、§18 對照、schema 版本）。

### 6. 資料預覽：正規名目錄 + 隱藏 staging 與中間表

**現況**：`data-preview.js:14-21` 只開 6 個自取的顯示標籤；`Domain/DataPreview.cs` 註明「不暴露實體資料表名」。

**設計**：把資料預覽改為**審計正規目錄**——列出可見的 canonical 表（`JE_PBC`、`TB_PBC`、`JE`、`TB`、`ACCOUNT_MAPPING`、`AUTHORIZED_PREPARER`、`DATE_DIMENSION`，以及呈現層的 `COMPLETENESS_*` 與 `*_OVERVIEW`，視需要開放），隱藏 staging、中間、結果、系統表。`DataPreviewDataset` 的白名單與標籤改為 canonical 名；有界預覽（最多 50 列加總數）維持不變。

**架構邊界**：仍是有界預覽，絕不載入完整母體；權威計算留在後端。

**影響檔案**：`wwwroot/js/data-preview.js`、`Domain/DataPreview.cs`、對應 repository、`action-contract-manifest.md`（query.dataPreview 的資料集）、`docs/jet-frontend-description.md`。

---

## 8. 契約變更彙總（contract-first，先改 manifest 再實作）

1. `project.create`：移除 `industry?`（對應第 5 項）。
2. Filter 情境 schema：加 KCT 來源標記；`invalid_scenario` 的 name/rationale 改為「非 KCT 來源必填」（對應第 2 項）。
3. Filter AST：新增 `specialAccountCategoryPair` 型別，加 `pairLogic` 與 `debitCategory`／`creditCategory`（對應第 3b 項）。
4. Calendar/Project 設定：加 `nonWorkingDays`（一週幾的集合，預設六日）（對應第 4 項）。
5. `query.dataPreview`：資料集改為 canonical 目錄（對應第 6 項）。

---

## 9. 測試計畫

- **Domain**：KCT 豁免判定、新配對條件的驗證（類別白名單加三模式決策表）、nonWorkingDays 的預設與邊界。
- **Infrastructure**：新配對述詞的命中身分（固定 fixture，三模式加否定）、參數化週末述詞（給定一週幾集合下的命中身分）、雙 provider parity。
- **Application**：wire 受理（KCT 豁免、新型別、nonWorkingDays、project.create 去掉 industry）。
- **改名不變量（關鍵）**：golden-master 差異測試——同一 demo 案件，改名前後 validate、prescreen、filter 結果完全相同；全套件 993 維持全綠。

---

## 10. 實作順序（workstream，各段自帶 build→test 驗證）

1. 第 1 項命名修正（零風險、獨立）。
2. 第 5 項建立案件欄位精簡（含契約）。
3. 第 4 項非工作日週幾選擇器（含參數化週末述詞）。
4. 第 2 項 KCT 獨立 A–J 面板加豁免（含契約）。
5. 第 3b 項新增「特殊科目類別配對」篩選條件（含契約）。
6. 第 3a 項科目配對序位（併入驗證步驟後段、完整性後解鎖）。
7. 第 7 項中央表名登錄、實體改名、v6 遷移（最大、最後做，用 golden-master 守住）。
8. 第 6 項資料預覽正規目錄（接在第 7 項之後）。
9. 文件回寫（guide、manifest、frontend-description、development-log、development-status、windows-handoff）加全套件驗證。

---

## 11. 待議 / Phase 2

- KCT 條件 B 加上獨立的 BS/IS 分類維度（卡在 KCT 交付分類表）。
- 完整性與總覽類是否由 CTE 落為實體表（`COMPLETENESS_*` 與 `*_OVERVIEW`）。
- `JE` 與 `JE_IN_PERIOD`／`JE_NOT_IN_PERIOD` 是否需要實體切分（目前期間切分在查詢層）。
