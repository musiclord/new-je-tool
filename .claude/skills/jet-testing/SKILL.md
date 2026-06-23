---
name: jet-testing
description: JET 專案的 AI 輔助測試開發規範。撰寫、修改、審查任何 src/JET/tests 下的測試，或評估測試涵蓋策略時必讀。明訂測試邊界、原則、設計技術與驗收方法。
---

# JET 測試開發規範 (Testing Boundaries, Principles, and Methods)

你是 JET 專案的測試工程師。AI 生成測試的品質上限，等於這份文件塞進去的測試工程知識。撰寫任何測試前，先讀完本文件；遇到與 `docs/jet-guide.md` 衝突之處，以 `docs/jet-guide.md` 為準並回報歧義。

## 0. 適用範圍與前置閱讀

- 測試專案：`src/JET/tests/JET.Tests/`（xUnit，net10.0-windows）。
- 前置閱讀：`docs/jet-guide.md`（資料驗證／預篩選／進階篩選條件的權威定義與 §4 命名登錄表）、`docs/action-contract-manifest.md`（action 的 wire shape）。規則一律以登錄表的具體名稱稱呼（`completeness_test`、`trailing_zeros`…），不用已退役的 V/R/A 代號。
- 驗證指令：

```bash
dotnet build src/JET/JET.slnx --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

## 1. 測試金字塔：先決定層級，再生成測試

每個情境在動手前必須先回答「這該落在哪一層」。能在低層驗的邏輯，禁止往高層搬。

| 層級 | 測什麼 | 測試形式 | 目錄 |
|:---|:---|:---|:---|
| **Domain（最厚）** | 純規則：金額縮放、門檻、欄位白名單、mapping 推斷、scenario 驗證 | 純 unit test，無 I/O、無 mock framework | `Domain/` |
| **Application** | handler 編排、use-case 流程、錯誤轉譯 | 經 `HandlerTestHost`（真 dispatcher + temp projects root）以 wire shape 斷言 | `Application/` |

> **ATDD 對應**：Application 層測試就是本專案的 acceptance test——驗收條件一律來自 `docs/action-contract-manifest.md` 的 action 契約（payload / response / error code），不另寫 Gherkin。新增或修改 action 時，先改 manifest，再依 manifest 寫紅燈驗收測試，最後實作 handler。
| **Infrastructure** | SQL 規則正確性、SQLite 存取、檔案讀寫、Excel 解析 | 對真 SQLite（temp file / in-memory）執行，禁止 mock DB | `Infrastructure/` |
| **Bridge（最薄）** | JSON 包裝、錯誤映射 | 只測傳輸與包裝，不重測業務規則 | `Bridge/` |

硬邊界：

- **禁止** WebView2 / WinForms E2E 自動化測試。`Form1.cs` 是 thin host，沒有可測的業務邏輯；UI 驗證走人工或 `/verify`。
- **禁止**為前端 JS 寫業務規則測試——前端本來就不准有業務規則（見 `AGENTS.md`），出現可測的前端規則本身就是架構違規，應回報而非補測。
- **禁止**在測試中以 C#/LINQ 重新實作 V/R/Filter 規則當作預期值來源（那只是用同一份錯誤驗證自己）。預期值必須來自 guide 的規格、手算的小型固定資料集，或獨立的 SQL 重跑。
- Mock 只允許用於 host boundary（如 `IHostShell`），以手寫 stub 為主；Domain 與 Infrastructure 測試不引入 mock framework。

## 2. 結構原則：FIRST + 既有慣例

生成的每個測試必須符合 FIRST：

- **Fast**：Domain 測試毫秒級；Infrastructure 測試用最小資料集（除非明確標註為 smoke/scale 測試）。
- **Independent**：測試彼此不依賴執行順序、不共用可變狀態。需要檔案系統就用 `TempProjectRoot` 模式，每個測試自己建、自己 `Dispose`。
- **Repeatable**：不依賴機器路徑、locale、系統時間、網路。日期一律用固定值。
- **Self-validating**：斷言寫死預期值，不准 `Assert.True(result != null)` 之類的弱斷言充數。
- **Timely**：規則類程式（Domain）優先採 test-first——先依 guide 規格寫測試，再實作。

既有慣例（與現有測試一致，不要另創風格）：

- 命名：`Method_Scenario_Expectation`，例如 `FromAverageMajorUnits_NonPositiveAverage_FloorsAtThree`。
- 一個測試驗一個行為；class 對應一個被測單元（`R4ThresholdTests` 對 `R4Threshold`）。
- 註解用繁體中文，只寫規格依據與不變量（如換算式），不寫流水帳。
- 魔術數字要嘛自我說明（`1_200_000m`），要嘛附上對應 guide 條文的註解。
- 需要組裝專案資料時，優先重用既有 helper（`HandlerTestHost`、`InlineWorkbookProject`、`DemoProjectPipeline`、`TestFixtures`），不要為同一件事另造新輪子。

## 3. 禁止的 test smells

生成後自查，出現以下任一項即重寫：

- **Assertion roulette**：一個測試塞多個不相關斷言，壞了不知是哪個。一個行為一個測試；同一行為的多面向斷言需逐一可辨識。**唯一例外：journey test**（如 `FullPipeline_*`、`FullMockPipeline_*`）——以使用者旅程驗收連續狀態轉換，分階段斷言屬同一行為鏈，但每個階段必須有註解標明，且不得在 journey test 裡夾帶可獨立成測的負向情境（負向情境另立測試）。
- **Mystery guest**：偷偷依賴外部檔案、共用資料庫、開發機路徑。唯一例外是 `RealDataSmokeTests` 這類明確標註、可條件跳過的真實資料煙霧測試。
- **Fragile test**：斷言綁在實作細節（私有結構、JSON 屬性順序、SQL 字串長相）而非可觀察行為。Application 層一律斷言 wire shape 的語意內容。
- **Happy-path-only**：只有成功路徑。每個 handler 測試組必須包含至少一條負向路徑（缺欄位、非法值、狀態不允許），並斷言其錯誤轉譯（`JetActionException` → bridge error shape）。
- **Conditional test logic**：測試內出現 `if/for` 分支邏輯。需要多組資料就用 `[Theory]` + `[InlineData]`。
- **重複測試（殺蟲劑悖論）**：不要再生一批與既有測試同形狀、只換數字的測試。新測試必須覆蓋新的等價類、邊界或風險，動手前先讀同目錄既有測試。

## 4. 測試設計技術：直接當成生成指令的骨架

模型不會主動套這些技術，所以規定如下——遇到對應形狀的需求，**必須**使用對應技術並在測試註解標明：

- **邊界值分析 (BVA)**：所有門檻與縮放規則（連續零尾數門檻、金額 scale、期末日界線、分頁 page size）。每個邊界產生「下鄰、邊界、上鄰」三點並標註預期接受/拒絕。
- **等價分割**：資料驗證規則、mapping 型別推斷。每個等價類取一代表值，並列出你劃分的等價類清單。
- **決策表**：多條件規則（R 規則之條件組合、filter scenario 的 AND/OR 組合）。先在註解列出決策表，再每列一個 `[InlineData]`。
- **狀態轉換**：專案工作流程（import → mapping → validation → prescreen → filter → export）。合法轉換各一測試，**非法轉換**（如未 mapping 就 validate）至少挑高風險者測拒絕行為。
- **Pairwise**：僅在參數組合爆炸時使用（如 provider × dataset kind × amount mode），並說明取捨。

## 5. Oracle 策略：預期值從哪來

「寫好斷言」的前提是知道正確答案。依情境選 oracle，並在測試註解寫明 oracle 來源：

1. **規格 oracle（首選）**：`docs/jet-guide.md` 的規則定義 + 手工可驗的小型固定資料集（≤ 20 rows），預期值可人工算出。
2. **Metamorphic 關係**：無法窮舉預期值時使用。本專案已知的不變量：
   - GL 分錄列順序對調，借貸不平測試結果不變、各規則命中集合不變。
   - 金額經 `MoneyScaling` 縮放再還原，應與原值一致（roundtrip）。
   - 對母體加入一筆「確定不命中」的分錄，規則命中筆數不變；加入一筆「確定命中」的，恰好 +1。
   - 同一規則以不同分頁大小取回的全部結果，聯集應相等。
3. **Property-based 性質**：縮放後金額為整數、白名單過濾後欄位 ⊆ 白名單、suggestion 引擎輸出永不含未知欄位。
4. **Golden master / differential**：legacy（`legacy/JET-legacy/`）遷移驗證時，以固定輸入凍結舊行為輸出做比對；SQL 規則可在 DB tool 獨立重跑作為 differential oracle（這正是 guide 規定規則必須是 SQL 的理由之一）。

## 6. 通過 ≠ 有效：生成後的品質檢驗

AI 生成的測試最常見的失效模式是「綠燈但抓不到 bug」。每批新測試交付前執行 mutation 思維自查：

- 對每個被測規則自問：「如果把 `>` 改成 `>=`、`SUM` 改成 `COUNT`、邊界常數 ±1，這批測試會紅嗎？」答不出「會」的測試，補資料點或斷言。
- 斷言必須鎖住**數值與身分**：規則命中測試要斷言「命中哪幾筆（doc_num/id）」，不能只斷言筆數。
- 交付說明中報告：覆蓋了哪些等價類/邊界、刻意不測什麼、為什麼。
- AI 生成的測試一律視為草稿：合入前必須經人工審查，確認斷言確實鎖住規格而非鎖住實作現狀。

**工具落地——突變測試（Stryker.NET）。** 上面的「mutation 思維」已有工具兜底:`.config/dotnet-tools.json` 裝 `dotnet-stryker`、設定 `stryker-config.json`(只突變 Domain 與 Application、`coverage-analysis: perTest`、`break: 0` 先不擋建置)。節奏是**階段性／里程碑前**對「本輪改動的 Domain/Application 檔」跑一次,把存活的 mutant 補掉斷言——不是每個小任務都跑(單次約數分鐘,多為固定開銷)。指令一律同時用 `-m` 指定改動的 `.cs` 檔(因 `--since` 遇到測試專案內非 `.cs` 改動會悄悄全跑):`dotnet stryker --since:main -m <改動檔>`。存活的 mutant 是「測試沒守住」的訊號;不追逐 100% 分數(等價 mutant 殺不掉),只補有意義的缺口。

**回應契約鎖。** 會把資料餵給前端的關鍵 action(`validate.run`、`prescreen.run`、`filter.preview`、`query.dataPreview`、`project.load`)在 `Application/ActionContractTests.cs` 以**手寫斷言**鎖住回應的「欄位集合＋型別」(不鎖屬性順序、不鎖數值,符合 §3 不綁實作細節)。新增或改 action 時**先更新契約**再改 handler(契約先行);改壞形狀測試就紅,擋住「悄悄加欄位／改名」讓沒有自動測試的前端默默顯示錯誤。不採快照工具,避免「差異一多就全部核可」被繞過。

## 7. 風險導向：先排序，再生成

測試投入依風險排序，高風險區密集生成，低風險區點到為止：

1. **最高**：金額正確性（`MoneyScaling`、scaled 整數運算、借貸不平測試）——錯了直接出具錯誤審計結論。
2. **高**：規則 SQL 語意（資料驗證／預篩選／進階篩選條件的命中集合正確性）、全母體語意（不得有隱性抽樣、截斷、分頁漏列）。
3. **中**：mapping 推斷與驗證、import 解析（Excel 邊角：空列、全形、千分位、負號格式）、工作流程狀態防呆。
4. **低**：demo data、wire shape 包裝細節、UI 文案。

跨 provider（SQLite / SQL Server）的 SQL 規則屬高風險：新增規則 SQL 時，至少在 SQLite 路徑用固定資料集驗證命中集合；SQL Server 路徑無法在本機跑時，明確說明跳過了什麼。SQL Server 閘控測試一律標 `[SqlServerFact]`／`[SqlServerTheory]`(見 `Infrastructure/SqlServerFact.cs`):偵測不到 LocalDB 時顯示為「略過(skipped)」而非 early-return 靜默通過,綠燈不說謊。

## 8. 測試任務的 prompt 模板（對人與對 AI 皆適用）

對本專案下測試指令時，必須包含五要素，缺一就先補齊再動工：

```
1. 測試目標：被測單元 + 對應 guide 條文（例：連續零尾數門檻 `trailing_zeros`，jet-guide.md §5）
2. 情境與流程：落在哪一層（Domain/Application/Infrastructure/Bridge）、入口是什麼
3. 輸出結構：測試 class/檔案位置、命名、Theory 或 Fact
4. 邊界與負向：指定設計技術（BVA/等價分割/決策表/狀態轉換）與必測的負向情境
5. 驗證規則：oracle 來源（規格手算/metamorphic/golden master）與斷言鎖定的內容（值＋身分）
```

弱指令範例（拒收）：「幫 filter 功能寫完整測試。」
強指令範例：「為 `FilterScenarioValidator` 寫 Domain 層測試：依 guide §6 的 AND/OR 巢狀規格列決策表，每列一個 `[InlineData]`；負向情境含空 scenario、未知欄位（必須被 `GlFieldWhitelist` 拒絕）、深度超限；斷言錯誤碼而非錯誤訊息字串。」

指令視同程式碼：第一輪輸出不符 §3-§6 就修指令重生，把缺漏的等價類、oracle 或負向情境補進指令本身，而不是手改生成結果了事。

## 9. ISTQB 七原則在本專案的落地解讀

- 測試只能證明缺陷存在 → 不得宣稱「已完整測試」，交付時改說「已覆蓋的等價類與邊界清單」。
- 窮舉不可能 → 用 §4 的設計技術選點，不靠堆量。
- 盡早測試 → Domain 規則先寫測試再實作；契約變更先改 manifest 再寫測試。
- 缺陷群聚 → bug 修復時在同模組鄰近邊界補測，不只補重現該 bug 的一條。
- 殺蟲劑悖論 → 見 §3「重複測試」禁令。
- 測試與情境相依 → 本專案的情境是審計工具：正確性 > 效能 > 體驗，測試密度依 §7 風險排序。
- 沒有錯誤不代表系統有用 → handler 測試以 `docs/action-contract-manifest.md` 的契約為驗收基準，而非「程式沒丟例外」。
