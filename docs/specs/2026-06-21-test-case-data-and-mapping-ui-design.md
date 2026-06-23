# 測試案件資料擴充與欄位配對介面優化 — 設計書

> 狀態:已實作,待使用者 GUI 人工驗收。本文為單一事實來源。
> 落地摘要:8 個 task 全數完成（由 subagent 驅動、全用 Opus 4.8、以 tree-diff 隔離而未 commit）;全套件 715 個測試綠（SQL Server parity 實跑過）;13 條規則的 seed 命中數，端到端對照 oracle 都精確成立。
> 已知問題:V1 的 part(a) 控制總數在完整管線（最後 commit TB）下永遠是 na。這是既有的 production 行為（`gl_control_total` 只在 GL 投影時寫入，TB 投影時 `RuleRunResultReset` 會清掉它且不重建），不在本案範圍內，留待後續評估。

## 目標

把使用者在實機驗收後提出的三項改善，落實成可驗收、可回歸的設計:

- **A. 配對介面命名白話化** — 兩種配對介面的切換名稱原本叫「經典三欄表 / 二維配對表」，太抽象、AI 感太重，改成「簡易清單 / 對照表格」。
- **B. 已提交配對改二維預覽** — 欄位配對「已提交」的綠燈摘要卡目前用 pill 標籤列出配對好的欄位，審計員看不出資料長什麼樣。改成二維資料表預覽:把來源欄當表頭，在其下標示它對應到哪個 JET 欄位，再附上幾列真實樣本資料。
- **C. 測試案件假資料擴充為 TDD oracle** — 把 demo 假資料工廠重新設計成「規模真實、且每條業務規則都有已知確定命中數」的測試母體，讓「跑一次測試案件，然後斷言每條規則的精確命中數」成為一道固定的測試與驗收程序。GL 要有 6000–8000 張傳票、TB 要有 100–200 個科目;公司名和傳票內容全部虛構，不得含任何真實機敏資料。

這三項共用同一個主題（優化測試案件的資料、流程、邏輯與配對體驗），所以合成一份 spec、一份 plan。實作時 A、B 是純前端小改，C 是資料層大改，計畫依此排序。

## 背景與現況（實證自原始碼）

- 配對介面的切換在 [mapping-step.js](../../src/JET/JET/wwwroot/js/steps/mapping-step.js) 的 `uiModeToggleHtml`，內部值是 `classic` / `grid`，顯示字串是「經典三欄表 / 二維配對表」。
- 已提交的摘要卡在同一檔的 `summarySection`，用 `mapping-summary__pill` 列出「欄位 ↔ 來源欄名」，不顯示資料、也不呈現二維表。
- demo 假資料工廠在 [DemoDataFactory.cs](../../src/JET/JET/Application/DemoDataFactory.cs):`GlRowCount = 2_000`（是**明細列**，不是傳票，約合 500 張傳票）、`TbAccountCount = 100`;用固定的 LCG 確定性生成，埋了部分規則種子。
- demo 走的是和使用者相同的 file-based 匯入管線（[DemoWorkbookWriter.cs](../../src/JET/JET/Infrastructure/DemoWorkbookWriter.cs) 寫出 xlsx，再由 `import.*.fromFile` 讀入）;測試共用的管線見 [DemoProjectPipeline.cs](../../src/JET/tests/JET.Tests/Application/DemoProjectPipeline.cs)。

### 現況的 oracle 缺口（本設計要修）

逐條核對 [RuleCatalog.cs](../../src/JET/JET/Domain/RuleCatalog.cs)、[GlRulePredicates.cs](../../src/JET/JET/Infrastructure/GlRulePredicates.cs)、[PrescreenRunHandler.cs](../../src/JET/JET/Application/PrescreenRunHandler.cs) 後確認以下問題:

1. **規則綠燈的條件是「命中數 > 0」**（`StatusOf`:count > 0 就是 `V`，否則 `na`）。要讓每條規則在 demo 都亮綠，demo 必須對每條都產生 **大於 0 且已知** 的命中。
2. **trailingZeros 的種子是錯的**:prescreen 的固定門檻是 6（`TrailingZeroThreshold.DefaultZerosThreshold = 6`），意思是「金額為 1,000,000 的整數倍」（`amount_scaled % 10^10 = 0`，moneyScale 是 10⁴）。現有種子金額 `500,000` 只有 5 個尾零，**不會命中**。
3. **R10 非授權編製人員無法觸發**:demo 和前端 mock 從來沒匯入過授權編製人員清單（`import.authorizedPreparer.fromFile`），所以 `HasAuthorizedPreparers = false`，永遠是 `na`。
4. **descNull 摘要空白沒有種子**:現有每一列都有摘要，所以 `blankDescription` 永遠是 0。
5. **R11 低頻編製者無法觸發**:5 名編製者平均分到約 2000 列，人人都超過 11，沒有人 ≤11。
6. **R3 未預期借貸組合、R12 低頻科目、R6 較少使用科目、週末/假日核准** 目前都靠隨機湊巧命中，命中數不可預測，沒辦法當 oracle。

## 開發原則與邊界（Global Constraints，全 task 適用）

照抄專案既有的鐵律（見 `AGENTS.md`、`docs/jet-guide.md`、CLAUDE.md），逐條對本案生效:

1. **分層架構**:Bridge/Form1 → Application → Domain ← Infrastructure;provider 分支只在 Infrastructure;前端零業務邏輯;`postMessage` 只在 `jet-api.js`。本案 A/B 只改前端呈現（無業務邏輯），C 的資料生成屬於 Application/Infrastructure。
2. **契約先行**:任何新增或更名的 action，先改 `docs/action-contract-manifest.md` 再動程式。
3. **雙 provider 等價**:任何新 SQL 或查詢，SQLite 和 SQL Server 都要端到端等價，並有 `[SqlServerFact]` 平價測試（LocalDB 閘控，沒設連線就乾淨 skip）。本案不新增規則 SQL，但新增的 demo 授權名單匯入要在兩個 provider 落地一致。
4. **TDD**:先寫會失敗的測試再實作;每條規則的 oracle 命中數，都用**獨立重算**（直接查 DB 的 set-based SQL，不經 handler）交叉驗證。
5. **確定性**:demo 工廠維持固定 LCG、不用時間種子;同版本程式產出要逐位元組相同（既有的 `Create_IsDeterministic` 不變量延續）。
6. **去機敏**:demo 全部欄位都是虛構值，公司名、人名、摘要都用通用佔位字串，**不得**出現任何真實客戶資料;真實樣本永遠只放本機的 `JET Test Case/`，絕不進 repo。
7. **版本控制**:執行階段不自行 commit/push;用 tree-diff 隔離審查;由使用者驗收後親自下版控指令。
8. **subagent 一律用 `claude-opus-4-8`（1M context）**:implementer、task-reviewer、fix、final reviewer 全部 Opus 4.8，無例外，不降 tier。

---

## A. 配對介面命名白話化

**改動**:`uiModeToggleHtml` 兩顆按鈕的顯示字串:

| 內部值（不變） | 舊顯示 | 新顯示 |
|---|---|---|
| `classic` | 經典三欄表 | **簡易清單** |
| `grid` | 二維配對表 | **對照表格** |

- 內部值 `classic` / `grid`、`Store.mappingUiMode` 狀態、所有判斷邏輯**都不變**。這純粹是顯示字串替換，對行為和契約零影響。
- 連帶清查:如果「經典三欄表」「二維配對表」這兩個字面字串還出現在註解、其他檔、測試或文件裡，一併更新成新名（用 grep 全庫定位）。`seg-toggle__label` 的「配對介面」維持不變。
- 不需要後端，也不需要改 manifest。

**測試角度**:本案沒有自動化前端測試框架（專案測試都是 C#）。驗收用人工:重開 app，到配對步驟看切換鈕是否顯示「簡易清單 / 對照表格」、切換行為是否如舊。實作者要在 plan 步驟用 grep 證明舊字串已無殘留。

---

## B. 已提交配對改二維預覽

**目標**:把 `summarySection`（已提交的綠燈卡）從 pill 清單改成「對照表加樣本資料列」，視覺上和 `grid` 編輯模式的二維表一致，但唯讀。

**呈現結構**（由上而下）:

1. 既有的狀態列與 facts（「已提交，依此配對執行後續測試」加上模式、已標準化列數、提交時間）——**保留**。
2. **二維對照表**（新）:
   - **表頭列**:來源欄位名（`importInfo.columns` 全部）。
   - **配對列**:每個來源欄之下，標示它被對應到的 JET 邏輯欄位顯示名（從 `committed.mapping` 反查:找出 `committed.mapping[fieldKey] === 該來源欄` 的那個欄位）。有對應的用綠底標示欄位名，沒對應的顯示中性的「(未對應)」。
   - **樣本資料列**:該資料集前幾列的真實內容（沿用 `ensureSourcePreview(kind, importInfo)` 與 `sourceCache`，和 `grid` 編輯模式共用同一份快取與載入路徑;`limit` 沿用既有的 10 列）;載入中、失敗、空批次都沿用既有的三態文案。
   - 表尾附「顯示前 N 列／共 M 列」，與 `twoDimMappingTable` 的 note 一致。
3. **字面值欄補述**（GL flag 模式的 `dcDebitCode`）:字面值不是來源欄、不進二維表，改在表下方用小字補述「借方代碼字面值 = 1」（值取自 `committed.mapping[dcDebitCode]`）。TB 沒有字面值欄就不顯示這段。
4. 動作鈕**保留**:「重新配對」「預覽標準化資料」。

**實作要點與邊界**:

- `summarySection` 目前的簽名是 `(kind, title, fields, modes, mappingState, mode)`，**沒收 `importInfo`**;需要從 `mappingSection` 把 `importInfo` 傳進來，才能拿到來源欄並載入樣本。
- 反查「來源欄 → JET 欄位」時要排除字面值欄（避免字面值剛好等於某個欄名時誤判），沿用 `mapping-step.js` 既有 `fieldForColumn` 的「先濾掉 literal key」做法，但比對對象改成 `committed.mapping`（不是 `draft`）。這裡抽一個小工具函式 `fieldForColumnIn(fields, mappingObj, column)` 給 draft 和 committed 共用。不過 DRY 的門檻是:同一段邏輯到第 2 處仍各自內聯，到第 3 處才抽。實際上 `fieldForColumn` 已經是 draft 版，本案新增的 committed 版只是第 2 處，所以可以直接內聯，不強制抽。
- `bindMappingSection`:摘要卡渲染時若有來源欄，就呼叫 `ensureSourcePreview(kind, Store.getState().importState[kind])` 觸發惰性載入（目前只在 `.map-grid` 存在時才呼叫，現在擴及摘要卡）。
- 唯讀:二維表裡不含任何 `<select>` / `data-map-col` / `data-mapping-key`，也不綁任何 change 事件。
- 前端零業務邏輯:仍然只讀 state 和既有的 `query.dataPreview`（經 `ensureSourcePreview`），不新增任何後端呼叫或契約。

**測試角度**:沒有前端測試框架。人工驗收:GL/TB 配對提交後，綠燈卡要呈現「來源欄表頭加對應 JET 欄位加樣本資料列」，字面值欄補述正確，「重新配對 / 預覽標準化資料」如舊;切回編輯（重新配對）再提交時，預覽要即時更新。實作者要核對:resume（`project.load`）回到已提交態時，樣本列能正常惰性載入（批次 `batchId` 對齊）。

---

## C. 測試案件假資料擴充為 TDD oracle（本案主體）

### C.1 規模目標（確定性固定值）

| 項目 | 現況 | 新值 | 範圍依據 |
|---|---|---|---|
| GL 傳票張數 | ~500 | **7,000 張**(固定) | 使用者裁定 6000–8000「傳票張數」,取中段固定值利於 oracle |
| GL 明細列(衍生) | 2,000 | **14,000 列**(7,000 × 2) | 每張固定 2 行(1 借 1 貸),seed 與 baseline 皆然;見 C.2 |
| TB 科目 | 100 | **150 個**(固定) | 使用者裁定 100–200,取中段 |
| 會計期間 | 2025 全年 | 2025-01-01 ~ 2025-12-31;`lastPeriodStart = 2025-12-31` | 不變 |
| MoneyScale | 10⁴ | 10⁴ | 沿用 Domain 預設 |

> 「每張固定 2 行」是 oracle 的關鍵簡化:傳票行數固定，每個 seed 群組貢獻的「列」命中數就能精確推算（見 C.3）。baseline 也用 2 行（1 借 1 貸），逐張平衡。

### C.2 生成模型:baseline + seed 兩層，命中數由建構保證

核心不變量（資料結構優先，消除特例）:

> **baseline 對每一條規則貢獻 0 命中;每個 seed 群組貢獻一個已知、文件化的命中數;所以每條規則的總命中數，就等於它那個 seed 群組的已知值。**

oracle 測試就是斷言「實跑命中數 == 文件化常數」。

#### baseline（乾淨基底，約 6,799 張）

每張 2 行（1 借 1 貸，逐張平衡），逐欄保證**不觸發任何規則**:

| 維度 | baseline 規則 | 不觸發的理由 |
|---|---|---|
| `post_date` | 平日(週一~五)、非假日、期內、且 ≤ 2025-12-20 | 不命中 週末/假日過帳、期外日期 |
| `approval_date`(核准) | = `post_date`(同日,故必為平日非假日,且 < 2025-12-31) | 不命中 週末/假日核准、期末後核准 |
| `voucher_date`(傳票登錄) | = `post_date` | `post_date < voucher_date` 不成立 → 不命中回溯 |
| `description`(摘要) | 取自安全摘要池(通用會計用語,不含任一 suspicious 關鍵字、非空白) | 不命中 摘要特定描述、摘要空白 |
| `created_by` | 取自「授權且高頻」編製者池(6 人,各 > 11 列) | 不命中 非授權、低頻編製者 |
| `amount` | 非 1,000,000 整數倍(永遠帶非零的百萬以下成分) | 不命中 連續零尾數(門檻 6) |
| 借貸科目 | 借方取「一般費用類(Others)」共用科目、貸方取「現金/Others」共用科目;**永不使用 Revenue 科目**;只用「共用科目集」(各保證 > 11 列) | 不命中 未預期借貸組合(無 Revenue 貸方)、低頻科目(共用科目皆高頻) |

安全摘要池（示意，通用詞，無關鍵字）:「進貨」「銷貨收入」「薪資費用」「租金支出」「水電費」「收回應收帳款」「支付應付帳款」「折舊提列」「利息收入」「運費」。
> 注意:suspicious 關鍵字含「調整/迴轉/沖銷/重分類/避險/重編/錯誤/計畫外/預算外/帳外」以及英文 ADJ/REV/RECLASS/SUSPENSE/ERROR/WRONG;安全池要逐一比對確認都不含。

#### seed（異常種子，每群組已知張數）

每個 seed 群組是一張最小的平衡傳票，只觸發它的目標規則（必要的共觸發在 C.3 明列），其餘維度一律照 baseline 的安全值填。

#### 科目宇宙（150 個）設計

- **具名科目**含各分類的錨點:Cash（1101 現金、1102 銀行存款）、Receivables（1131 應收帳款、1141 應收票據）、Receipt in advance（2251 預收貨款，**新增**這個來補齊對方分類）、Revenue（4101 銷貨收入、4111 銷貨退回、4201 利息收入），其餘具名科目與填充科目歸 Others。
- **共用科目集**:baseline 和多數 seed 使用的高頻科目（各 > 11 列，確定性保證）。
- **稀有科目集**:3 個科目，只出現在 R12 種子裡（每個恰好 ≤ 11 列）;baseline 和其他 seed **永不**使用，因此保證「GL 裡 ≤11 列的科目」剛好就是這 3 個。
- 科目配對檔（`target_account_mapping`）涵蓋全部 150 個科目，分類由科目代號確定性派生（沿用現有白名單規則，再加 2251→Receipt in advance、4 開頭→Revenue）。
- TB 由 GL 借貸合計推導（沿用 `BuildTbRows`），所以完整性（V1）恆過（`tb_s == gl_s` 逐科目成立）;TB 有而 GL 無活動的科目，`tb_s = gl_s = 0`，也不產生差異。

#### 編製者設計

| 角色 | 人數 | 列數 | 觸發 |
|---|---|---|---|
| 授權且高頻 | 6 | 各數百列(baseline 主力) | 無(乾淨) |
| 授權但低頻「稀有編製者」 | 1 | 恰 10 列(5 張 × 2 行,≤11) | **僅 R11**(在授權清單內 → 不命中 R10) |
| 非授權「未授權者」 | 1 | 32 列(16 張 × 2 行,>11) | **僅 R10**(>11 → 不命中 R11) |

授權清單（`target_authorized_preparer`）= 6 名高頻 + 1 名低頻 = 7 人;「未授權者」**不**在清單內。這樣 R10 和 R11 落在不同人身上，命中數彼此獨立，可以分開斷言。

### C.3 規則覆蓋矩陣（oracle）

下表每一列就是一條 oracle 斷言。命中數以「列（target_gl_entry 行）」計;常數是設計值，實作時定成具名常數（plan 鎖定）。

| 規則(key) | 觸發述詞(摘要) | seed 機制 | seed 張數 | 預期命中(列) | 共觸發 |
|---|---|---|---|---|---|
| postPeriodApproval (R1) | `approval_date >= lastPeriodStart` | 核准日 = 2026-01-15(週四、非假日) | 20 | 40 | 無 |
| suspiciousKeywords (R2) | 摘要含關鍵字 | 借方行摘要 = 「調整分錄」 | 25 | 25(僅該行) | 無 |
| unexpectedAccountPair (R3) | 同傳票 Revenue 貸 + 對方分類借 | 借 應收帳款 / 貸 銷貨收入 | 30 | 60(兩行皆 tag) | 無 |
| trailingZeros (R4) | `amount_scaled % 10^10 = 0` | 金額 = 2,000,000(借貸各一) | 15 | 30 | 無 |
| weekendPosting (R7) | 過帳日週六/日且非補班 | 過帳日 = 固定週六集(非補班、非假日) | 12 | 24 | 無 |
| weekendApproval (R7) | 核准日週六/日且非補班 | 核准日 = 2025-06-14(週六);過帳平日 | 10 | 20 | 無 |
| holidayPosting (R8) | 過帳日 ∈ 假日曆 | 過帳日 ∈ {2025-02-28,04-04,05-01,10-10}(皆平日假日) | 14 | 28 | 無 |
| holidayApproval (R8) | 核准日 ∈ 假日曆 | 核准日 = 2025-04-04(平日假日,< 期末);過帳平日 | 8 | 16 | 無 |
| blankDescription (descNull) | 摘要 NULL 或空白 | 借方行摘要 = 空字串 | 18 | 18(僅該行) | **V4 nullDescription = 18(設計內)** |
| backdatedPosting (R9) | `voucher_date 非空 且 post_date < voucher_date` | 傳票登錄日 = 過帳日 + 3 | 22 | 44 | 無 |
| nonAuthorizedPreparer (R10) | 編製者非空且不在授權清單 | created_by =「未授權者」 | 16 | 32 | 無 |
| lowFrequencyPreparer (R11) | 編製者全期列數 ≤ 11 | created_by =「稀有編製者」 | 5 | 10 | 無 |
| lowFrequencyAccount (R12) | 科目全期列數 ≤ 11 | 3 個稀有科目,各借方行 × 2 張(貸方走共用科目) | 6 | 6 | 無 |

彙總規則（不是列述詞，只需要狀態為 V 和穩定的彙總，不是精確列數 oracle）:
- **creatorSummary (R5)**:編製者 ≥ 8 人（6+1+1），`status=V`。oracle 斷言「列裡含『未授權者』『稀有編製者』，且其 entryCount 等於上表的設計值」。
- **rareAccounts (R6)**:`DistinctAccountCount > 0`、`status=V`;oracle 斷言彙總裡含那 3 個稀有科目。

驗證類（V1–V4）的 oracle:
- **V1 完整性**:差異科目數 = 0（TB 由 GL 推導）;part(a) 控制總數的 `RowCountMatch && AmountMatch` 為真。
- **V2 借貸不平**:不平傳票數 = 0（每張 seed 和 baseline 都平衡）。
- **V3 INF 抽樣**:樣本數 = 請求的 SampleSize（母體遠大於樣本）。
- **V4 空值紀錄**:`nullAccount = 0`、`nullDocument = 0`、`outOfRangeDate = 0`、**`nullDescription = 18`**（= blankDescription 的 seed，這是設計內的共觸發，已文件化）。

> **共觸發政策**:除了「摘要空白 → V4 nullDescription」這一個無法分離的設計內共觸發之外，每個 seed 群組只觸發它的目標規則。實作者要在 oracle 測試裡，對「非目標規則不因本群組而增加」做反向防呆（例如:weekend 種子不能讓任何 holiday 計數變動）。

#### 無意外共觸發的證明義務（plan 必須逐項落實成斷言）

1. baseline 金額永遠不是 1,000,000 的整數倍，所以 trailingZeros 只來自 R4 seed。
2. baseline 和其他 seed 的日期都是平日非假日、核准日 < 期末、登錄日 = 過帳日，所以週末/假日/期末後/回溯都只來自各自的 seed。
3. baseline 不用 Revenue 科目;除了 R3 之外，沒有任何傳票同時有「Revenue 貸 + 對方借」，所以 unexpectedAccountPair 只來自 R3。
4. 稀有科目只出現在 R12 seed、稀有編製者只出現在 R11 seed、未授權者只出現在 R10 seed，所以這三者的命中數精確且互斥。
5. 安全摘要池和關鍵字集沒有交集，baseline 也沒有空摘要，所以 suspiciousKeywords 和 blankDescription 都只來自各自的 seed。

### C.4 去機敏保證

- 公司名改成明確虛構的 `範例製造股份有限公司`（原本的 `德謨…` 也是虛構，改名是為了讓「非真實」一目了然）;`ProjectCode`、`OperatorId`、編製者姓名（王小明等通用佔位名加「稀有編製者」「未授權者」）、摘要（通用會計用語）全部虛構。
- 新增防護測試:斷言 demo 的 `EntityName` 等於固定的虛構常數;並文件化「demo 工廠不得貼入任何真實客戶資料，真實樣本只放本機 `JET Test Case/`」。
- 真實樣本相關的本機閘控測試（如 `PbcRealDataSmokeTests`）不受本案影響（仍讀本機、不入 repo）。

### C.5 效能緩解（使用者裁定「單一大資料、全測試套件共用」）

大資料會放大 demo 的建置成本（每個走管線的測試類別都會重建一次）。以下緩解措施讓套件維持可用，又不改變「單一資料集」這個決定:

1. **記憶化 `DemoDataFactory.Create()`**:回傳一個確定性、不可變的快取單例（`loadDemo`、三個 `demo.export*`、授權名單匯出每次都會呼叫 `Create()`，在大資料下重複生成很浪費）。確定性不變，並用 `Lazy<DemoProjectData>` 保證執行緒安全。
2. **demo 檔案寫出做 process 靜態記憶化**:`DemoWorkbookWriter` 的 GL/TB/科目配對/授權名單寫出，各用 process 靜態的 `Lazy<DemoExportedFile>` 包起來，整個測試行程**只寫一次** xlsx、跨所有 test class 重用同一個檔（內容確定性、唯讀重用安全;`Lazy` 把寫入序列化成單次，化解原本「並行寫入互鎖」的疑慮）。維持用 ClosedXML，**不另寫 SAX 寫出器**（這樣比較簡單、風險低，而且比逐類別重寫更有效地消除大資料的寫出成本）。
3. **測試 fixture 一次性建置**:沿用 `DemoProjectFixture`（class fixture，每個類別建一次）;新的 oracle 測試也走 class fixture，把 GL/TB/mapping/calendar/authorized 一次匯入，多個斷言共用。

### C.6 新增 / 異動的擴充點

**契約（先改 manifest）**:
- 新增 action `demo.exportAuthorizedPreparerFile`（照 `demo.exportAccountMappingFile` 寫:回傳 `{ filePath, fileName }`），讓 demo/mock 能用 file-based 管線匯入授權名單（`import.authorizedPreparer.fromFile` 只收 file，沒有 dates 變體）。

**Domain / Application**:
- `DemoProjectData` 新增 `AuthorizedPreparers`（姓名清單）、`AuthorizedPreparerFileName`、`AuthorizedPreparerColumns`。
- `DemoDataFactory`:把生成邏輯全面重寫成 C.2 的模型;`GlVoucherCount = 7_000`、`TbAccountCount = 150`、每個 seed 常數都具名;`Create()` 做記憶化。
- `project.loadDemo` 的回應**維持現狀**（前端 mock 是用 file 匯入，不需要把名單放進 response）;`demoScenario` 維持。
- `IDemoFileWriter` 新增 `WriteAuthorizedPreparerAsync`;`DemoWorkbookWriter` 實作（ClosedXML，單欄姓名）;四種檔案寫出都用 process 靜態 `Lazy` 記憶化（維持 ClosedXML，見 C.5）。
- 新增 `DemoExportAuthorizedPreparerFileHandler`;`AppCompositionRoot` 註冊;`jet-api.js` 的 `SUPPORTED_ACTIONS` 加進去。

**前端 mock 流程**（[import-step.js](../../src/JET/JET/wwwroot/js/steps/import-step.js) 的 `mockImportData`）:
- 在科目配對之後加一段:`demo.exportAuthorizedPreparerFile` → `import.authorizedPreparer.fromFile` → `Store.setAuthorizedPreparerState(...)`;完成訊息補上「授權編製人員 N 位」。

**測試管線**（[DemoProjectPipeline.cs](../../src/JET/tests/JET.Tests/Application/DemoProjectPipeline.cs) 的 `SetupAsync`）:
- 新增兩個可選旗標 `importAccountMapping`（預設 true）、`importAuthorizedPreparer`（預設 true），用來匯入科目配對與授權名單，讓完整的 oracle 可以被斷言。預設改成「完整匯入」以涵蓋 R3/R10;既有測試若依賴「無科目配對 → unexpectedAccountPair=na」，要改用旗標關閉（plan 逐一處理）。

## 測試策略（每一處都要有測試角度）

1. **DemoDataFactory 單元測試（C# 直測資料物件，不進 DB）**:
   - 規模:傳票張數 = 7,000、TB 科目 = 150、GL 列數 = 設計總和（精確值）。
   - 確定性:`Create()` 連跑兩次序列化要逐位元組相同（沿用既有）。
   - 逐張平衡、過帳日都在 2025、TB 由 GL 推導（沿用既有，更新期望值）。
   - **建構不變量**（對應 C.3 的證明義務）:baseline 金額無 1,000,000 倍數;baseline 不含 Revenue 科目;稀有科目恰 3 個且各 ≤11 列;稀有編製者恰 10 列且在授權名單;未授權者 >11 列且不在授權名單;安全摘要池與關鍵字無交集。
   - 去機敏:`EntityName` == 固定虛構常數。
2. **規則 oracle 測試（端到端，經 handler，雙 provider）**——這是本案 TDD 的驗收核心:
   - 走完整管線建出 demo 專案（GL/TB/mapping/calendar/authorized 加 commit），呼叫 `prescreen.run` 和 `validate.run`。
   - 對 C.3 矩陣的**每一條**斷言 `status == "V"` 且 `count ==` 設計常數;V1–V4 斷言上述的精確值。
   - **獨立重算交叉驗證**:對每條規則另外用「直接查 DB 的 set-based SQL」（`DemoProjectPipeline.QueryScalarAsync`）重算命中數，要和 handler 的回應一致（避免 oracle 與被測同源時的自證）;重算 SQL 和述詞語意相同但獨立書寫。
   - **反向防呆**:抽樣斷言「非目標 seed 不污染其他規則」（例如移除 weekend seed 概念上不影響 holiday）——由「各規則命中數都是彼此獨立的設計值」這個整體一致性涵蓋。
   - **`[SqlServerFact]` 平價**:同一組斷言在 SQL Server 重跑（LocalDB 閘控、乾淨 skip）;含 `Count > 0` 假綠防呆（母體確實落地了才比對）。
3. **效能不回歸的可觀察性**:oracle 測試走 class fixture 一次建置;plan 要記錄「全套件在設定 `JET_SQLSERVER_CONNECTION` 下實跑」的前後測試數與耗時，當作驗收附註（不是硬性門檻，但如果從數秒暴增到數分鐘要回報）。
4. **既有測試的影響範圍（plan 須逐檔處理，不得遺漏）**:硬編 `2_000` / `100`、或依賴舊 demo 行為的檔案，至少有 `DemoDataFactoryTests`、`ProviderParityJourneyTests`、`PrescreenRunHandlerTests`、`ValidateRunHandlerTests`、`GlRuleSqlEquivalenceTests`、`DemoHandlersTests`、`ImportProgressEventTests`、`GlRowProjectorTests`、`InlineWorkbookProject`、`WorkpaperExportHandlerTests`、`PbcRealDataSmokeTests`;plan 要為每個檔列出預期更新，並以實跑綠燈收斂。

## 非目標（YAGNI）

- 不改任何規則述詞 / SQL / 門檻語意（只修「demo 種子打不中 trailingZeros」這個 fixture 錯誤，不是改規則）。
- 不新增規則，不改 prescreen/filter/validate 的契約形狀。
- 不動科目配對的「流程設計」（審計員溝通配對流程仍暫緩;本案只擴充 demo 科目配對的**資料**與既有匯入，不擴張那個功能）。
- 不為前端 A/B 引入前端測試框架。
- 不改真實樣本本機閘控測試的讀取方式。

## 已解決的決策（來自互動確認）

1. GL 量級 = **傳票張數**（7,000 張固定）。
2. 測試策略 = **單一大資料、全套件共用**（用 C.5 緩解效能）。
3. 介面命名 = **簡易清單 / 對照表格**。
4. 已提交預覽 = **對照表 + 樣本資料列**。
