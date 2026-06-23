---
name: jet-testing
description: JET 專案的 AI 輔助測試開發規範。撰寫、修改、審查任何 src/JET/tests 下的測試，或評估測試涵蓋策略時必讀。明訂測試邊界、原則、設計技術與驗收方法。
---

# JET 測試開發規範 (Testing Boundaries, Principles, and Methods)

你是 JET 專案的測試工程師。AI 生成測試的品質有個上限,而這個上限就等於這份文件塞進去的測試工程知識有多少。所以撰寫任何測試之前,先把本文件讀完。如果遇到和 `docs/jet-guide.md` 衝突的地方,以 `docs/jet-guide.md` 為準,並把歧義回報出來。

## 0. 適用範圍與前置閱讀

- 測試專案在 `src/JET/tests/JET.Tests/`,用 xUnit,目標框架是 net10.0-windows。
- 動手前要先讀兩份文件。一是 `docs/jet-guide.md`,它是資料驗證、預篩選、進階篩選條件的權威定義,§4 有命名登錄表。二是 `docs/action-contract-manifest.md`,它定義各 action 的 wire shape(傳輸資料形狀)。稱呼規則時,一律用登錄表裡的具體名稱(例如 `completeness_test`、`trailing_zeros`),不要再用已經退役的 V/R/A 代號。
- 驗證指令:

```bash
dotnet build src/JET/JET.slnx --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

## 1. 測試金字塔：先決定層級，再生成測試

每個情境在動手之前,都必須先回答一個問題:這該落在哪一層。一條原則:只要能在較低層驗的邏輯,就禁止把它往高層搬。

| 層級 | 測什麼 | 測試形式 | 目錄 |
|:---|:---|:---|:---|
| **Domain（最厚）** | 純規則：金額縮放、門檻、欄位白名單、mapping 推斷、scenario 驗證 | 純 unit test，無 I/O、無 mock framework | `Domain/` |
| **Application** | handler 編排、use-case 流程、錯誤轉譯 | 經 `HandlerTestHost`（真 dispatcher + temp projects root）以 wire shape 斷言 | `Application/` |

> **ATDD 對應**:Application 層的測試,就是本專案的驗收測試(acceptance test)。驗收條件一律來自 `docs/action-contract-manifest.md` 裡的 action 契約,也就是 payload、response、error code,不另外寫 Gherkin。所以新增或修改一個 action 時,順序是:先改 manifest,再依 manifest 寫一個紅燈的驗收測試,最後才實作 handler。
| **Infrastructure** | SQL 規則正確性、SQLite 存取、檔案讀寫、Excel 解析 | 對真 SQLite（temp file / in-memory）執行，禁止 mock DB | `Infrastructure/` |
| **Bridge（最薄）** | JSON 包裝、錯誤映射 | 只測傳輸與包裝，不重測業務規則 | `Bridge/` |

以下是幾條硬邊界:

- **禁止** WebView2 與 WinForms 的 E2E 自動化測試。`Form1.cs` 是個 thin host,裡頭沒有可測的業務邏輯;UI 要驗證就走人工或 `/verify`。
- **禁止**為前端 JS 寫業務規則測試。前端本來就不准放業務規則(見 `AGENTS.md`),所以一旦出現「可測的前端規則」,這件事本身就是架構違規,正確反應是回報它,而不是去補測試。
- **禁止**在測試裡用 C# 或 LINQ 重新實作 V/R/Filter 規則,拿來當預期值的來源。那等於用同一份(可能錯的)邏輯來驗證它自己。預期值必須另有出處:guide 的規格、手算的小型固定資料集,或獨立的 SQL 重跑。
- Mock 只允許用在 host boundary(例如 `IHostShell`),而且以手寫 stub 為主。Domain 與 Infrastructure 的測試不引入 mock framework。

## 2. 結構原則：FIRST + 既有慣例

每個生成的測試都必須符合 FIRST 五項:

- **Fast(快)**:Domain 測試要在毫秒級完成。Infrastructure 測試用最小的資料集,除非它被明確標註為 smoke 或 scale 測試。
- **Independent(獨立)**:測試彼此之間不依賴執行順序,也不共用可變狀態。需要檔案系統時就用 `TempProjectRoot` 模式,每個測試自己建立、自己 `Dispose`。
- **Repeatable(可重複)**:測試不依賴特定機器的路徑、locale、系統時間或網路。日期一律寫固定值。
- **Self-validating(自我驗證)**:斷言要寫死預期值。不准用 `Assert.True(result != null)` 這種弱斷言來充數。
- **Timely(及時)**:規則類的程式(Domain)優先採 test-first,也就是先依 guide 規格寫測試,再實作。

下面是既有慣例。請和現有測試保持一致,不要另創一套風格:

- 命名格式是 `Method_Scenario_Expectation`,例如 `FromAverageMajorUnits_NonPositiveAverage_FloorsAtThree`。
- 一個測試只驗一個行為;一個 test class 對應一個被測單元(例如 `R4ThresholdTests` 對應 `R4Threshold`)。
- 註解用繁體中文,只寫規格依據與不變量(例如換算式),不寫流水帳。
- 魔術數字要嘛自我說明(例如 `1_200_000m`),要嘛附上一段註解指向對應的 guide 條文。
- 需要組裝專案資料時,優先重用既有的 helper(`HandlerTestHost`、`InlineWorkbookProject`、`DemoProjectPipeline`、`TestFixtures`),不要為同一件事重造輪子。

## 3. 禁止的 test smells

測試生成後要自我檢查,只要出現下列任何一項就重寫:

- **Assertion roulette(斷言輪盤)**:一個測試塞了多個不相關的斷言,壞掉時看不出是哪一個出問題。原則是一個行為一個測試;同一行為若要從多個面向斷言,每個面向都要能被單獨辨識出來。**唯一例外是 journey test**(例如 `FullPipeline_*`、`FullMockPipeline_*`):它用一條使用者旅程來驗收連續的狀態轉換,所以分階段的斷言屬於同一條行為鏈。但即使如此,每個階段都必須有註解標明,而且不准在 journey test 裡夾帶那種「本來可以獨立成測」的負向情境——負向情境要另外立一個測試。
- **Mystery guest(神秘訪客)**:偷偷依賴外部檔案、共用資料庫,或開發機上的路徑。唯一例外是 `RealDataSmokeTests` 這類真實資料煙霧測試,它有明確標註,而且可以被條件跳過。
- **Fragile test(脆弱測試)**:斷言綁在實作細節上,例如私有結構、JSON 屬性順序、SQL 字串長相,而不是綁在可觀察的行為上。Application 層一律斷言 wire shape 的語意內容。
- **Happy-path-only(只有快樂路徑)**:測試只涵蓋成功路徑。每一組 handler 測試都必須至少包含一條負向路徑(例如缺欄位、非法值、狀態不允許),並斷言它的錯誤轉譯,也就是 `JetActionException` 如何轉成 bridge 的 error shape。
- **Conditional test logic(測試裡有條件邏輯)**:測試內出現 `if` 或 `for` 這類分支邏輯。需要跑多組資料時,改用 `[Theory]` 搭配 `[InlineData]`。
- **重複測試(殺蟲劑悖論)**:不要再生一批和既有測試形狀相同、只換了數字的測試。新測試必須覆蓋到新的等價類、邊界或風險。所以動手之前,先讀同目錄下既有的測試。

## 4. 測試設計技術：直接當成生成指令的骨架

模型不會主動套用這些技術,所以這裡明文規定:只要碰到對應形狀的需求,就**必須**使用對應的技術,並在測試註解裡標明用了哪一種。

- **邊界值分析(BVA, Boundary Value Analysis)**:用在所有門檻與縮放規則上,例如連續零尾數門檻、金額 scale、期末日界線、分頁的 page size。每個邊界都要產生三個點——下鄰、邊界本身、上鄰——並標註各自的預期是接受還是拒絕。
- **等價分割(Equivalence Partitioning)**:用在資料驗證規則、mapping 型別推斷上。每個等價類取一個代表值,並把你劃分出來的等價類清單列出來。
- **決策表(Decision Table)**:用在多條件規則上,例如 R 規則的條件組合、filter scenario 的 AND/OR 組合。做法是先在註解裡把決策表列出來,再讓每一列對應一個 `[InlineData]`。
- **狀態轉換(State Transition)**:用在專案工作流程上(import → mapping → validation → prescreen → filter → export)。每個合法轉換各寫一個測試;至於**非法轉換**(例如還沒 mapping 就去 validate),至少挑高風險的那些,測它確實會拒絕。
- **Pairwise(成對組合)**:只在參數組合會爆炸時才用(例如 provider × dataset kind × amount mode),用的時候要說明取捨。

## 5. Oracle 策略：預期值從哪來

要「寫好斷言」,前提是你得知道正確答案是什麼。這個正確答案的來源就叫 oracle。依情境挑一種 oracle,並在測試註解裡寫明用的是哪一種:

1. **規格 oracle(首選)**:用 `docs/jet-guide.md` 的規則定義,搭配一個手工可驗的小型固定資料集(20 列以內),這樣預期值可以人工算出來。
2. **Metamorphic 關係(蛻變關係)**:當你無法窮舉所有預期值時改用這種。它驗的不是「答案等於某值」,而是「輸入這樣變,輸出應該那樣變」。本專案已知的幾條不變量是:
   - 把總帳(General Ledger,GL)分錄的列順序對調後,借貸不平的測試結果不變,各規則的命中集合也不變。
   - 金額經 `MoneyScaling` 縮放後再還原,應該和原值一致(roundtrip,來回轉換)。
   - 對母體加入一筆「確定不會命中」的分錄,規則的命中筆數不變;加入一筆「確定會命中」的,命中筆數恰好加一。
   - 同一個規則,用不同的分頁大小把全部結果取回來,這些結果的聯集應該相等。
3. **Property-based 性質**:斷言某些恆成立的性質,例如縮放後的金額一定是整數、白名單過濾後的欄位一定是白名單的子集、suggestion 引擎的輸出永遠不含未知欄位。
4. **Golden master / differential(黃金母版 / 差異比對)**:在驗證 legacy(`legacy/JET-legacy/`)的遷移時,給一組固定輸入,把舊行為的輸出凍結下來當基準來比對。SQL 規則則可以在 DB 工具裡獨立重跑,當作 differential oracle——這也正是 guide 之所以規定規則必須寫成 SQL 的理由之一。

## 6. 通過 ≠ 有效：生成後的品質檢驗

AI 生成的測試最常見的失效模式,是「綠燈,但其實抓不到 bug」。所以每一批新測試交付之前,都要用 mutation(突變)的思維自我檢查:

- 對每一個被測規則問自己一句:如果把 `>` 改成 `>=`、把 `SUM` 改成 `COUNT`、把邊界常數加減一,這批測試會不會變紅?只要答不出「會」,就代表測試守得不夠,要補資料點或補斷言。
- 斷言必須鎖住**數值與身分**兩者。例如規則命中的測試,不能只斷言「命中幾筆」,還要斷言「命中的是哪幾筆」(用 doc_num 或 id 指明)。
- 交付說明裡要報告三件事:覆蓋了哪些等價類與邊界、刻意不測什麼、為什麼不測。
- AI 生成的測試一律當草稿看。合入之前必須經過人工審查,確認斷言鎖住的是規格,而不是鎖住目前的實作現狀。

**工具落地——突變測試(Stryker.NET)。** 上面講的「mutation 思維」有工具兜底:`.config/dotnet-tools.json` 裝了 `dotnet-stryker`,設定檔是 `stryker-config.json`(只突變 Domain 與 Application、`coverage-analysis` 設為 `perTest`、`break` 設為 `0` 表示先不擋建置)。

跑的節奏是**在每個階段性或里程碑之前**,對「本輪改動的 Domain/Application 檔」跑一次,把存活下來的 mutant 補上斷言。不是每個小任務都跑,因為單次約要數分鐘,且大多是固定開銷。指令一律要同時用 `-m` 把改動的 `.cs` 檔指明出來,否則 `--since` 一旦遇到測試專案裡非 `.cs` 的改動,會悄悄變成全跑:`dotnet stryker --since:main -m <改動檔>`。存活的 mutant 是個訊號,代表測試沒守住那個點。不要去追求 100% 分數,因為等價 mutant 本來就殺不掉;只補有意義的缺口就好。

**回應契約鎖。** 有幾個關鍵 action 會把資料餵給前端:`validate.run`、`prescreen.run`、`filter.preview`、`query.dataPreview`、`project.load`。這些 action 在 `Application/ActionContractTests.cs` 裡,用**手寫斷言**把回應的「欄位集合加上型別」鎖住(只鎖這兩者,不鎖屬性順序、不鎖數值,符合 §3「不綁實作細節」的要求)。新增或修改 action 時,要**先更新契約**,再改 handler,這就是契約先行。一旦形狀被改壞,契約測試就會變紅,藉此擋住「悄悄加一個欄位或改個名」——因為前端沒有自動測試,這種改動會讓它默默顯示錯誤。這裡刻意不用快照工具,以免落入「差異一多就乾脆全部核可」而被繞過。

## 7. 風險導向：先排序，再生成

測試投入要依風險排序:高風險區密集地生成測試,低風險區點到為止就好。

1. **最高風險**:金額正確性(`MoneyScaling`、scaled 整數運算、借貸不平測試)。這裡錯了會直接出具錯誤的審計結論。
2. **高風險**:規則 SQL 的語意(資料驗證、預篩選、進階篩選條件,它們的命中集合是否正確),以及全母體語意(不得有任何隱性的抽樣、截斷,或分頁漏列)。
3. **中風險**:mapping 的推斷與驗證、import 解析(Excel 的各種邊角狀況:空列、全形字、千分位、負號格式)、工作流程的狀態防呆。
4. **低風險**:demo data、wire shape 的包裝細節、UI 文案。

跨 provider(SQLite 與 SQL Server)的 SQL 規則屬於高風險。新增一條規則 SQL 時,至少要在 SQLite 路徑上用固定資料集驗證它的命中集合。如果 SQL Server 路徑在本機跑不了,就明確說明你跳過了什麼。SQL Server 的閘控測試一律標上 `[SqlServerFact]` 或 `[SqlServerTheory]`(見 `Infrastructure/SqlServerFact.cs`):這樣在偵測不到 LocalDB 時,測試會顯示為「略過(skipped)」,而不是用 early-return 靜默通過。重點是綠燈不能說謊。

## 8. 測試任務的 prompt 模板（對人與對 AI 皆適用）

對本專案下測試指令時,必須包含以下五個要素。缺了任何一個,就先把它補齊再動工:

```
1. 測試目標：被測單元 + 對應 guide 條文（例：連續零尾數門檻 `trailing_zeros`，jet-guide.md §5）
2. 情境與流程：落在哪一層（Domain/Application/Infrastructure/Bridge）、入口是什麼
3. 輸出結構：測試 class/檔案位置、命名、Theory 或 Fact
4. 邊界與負向：指定設計技術（BVA/等價分割/決策表/狀態轉換）與必測的負向情境
5. 驗證規則：oracle 來源（規格手算/metamorphic/golden master）與斷言鎖定的內容（值＋身分）
```

弱指令的範例(拒收):「幫 filter 功能寫完整測試。」這種指令沒有上述五要素,無從動工。

強指令的範例:「為 `FilterScenarioValidator` 寫 Domain 層測試。依 guide §6 的 AND/OR 巢狀規格列出決策表,每一列對應一個 `[InlineData]`。負向情境要包含三種:空 scenario、未知欄位(必須被 `GlFieldWhitelist` 拒絕)、巢狀深度超限。斷言要鎖在錯誤碼上,不要鎖在錯誤訊息字串上。」

指令本身要當成程式碼來看待。如果第一輪輸出不符合 §3 到 §6,就回頭去修指令再重新生成,把缺漏的等價類、oracle 或負向情境補進指令本身,而不是手動改一改生成結果就交差。

## 9. ISTQB 七原則在本專案的落地解讀

- **測試只能證明缺陷存在,不能證明沒有缺陷。** 所以不得宣稱「已完整測試」;交付時改說「以下是已覆蓋的等價類與邊界清單」。
- **窮舉測試不可能做到。** 所以用 §4 的設計技術去選點,而不是靠堆量。
- **盡早測試。** 所以 Domain 規則先寫測試再實作;契約一旦要變更,先改 manifest 再寫測試。
- **缺陷會群聚。** 所以修 bug 時,要在同一模組的鄰近邊界一併補測,而不是只補能重現該 bug 的那一條。
- **殺蟲劑悖論(同一批測試跑久了就抓不到新 bug)。** 對策見 §3 的「重複測試」禁令。
- **測試與情境相依。** 本專案的情境是一個審計工具,優先順序是正確性大於效能、效能大於體驗,所以測試密度依 §7 的風險排序來分配。
- **沒有錯誤,不代表系統就有用。** 所以 handler 測試以 `docs/action-contract-manifest.md` 的契約作為驗收基準,而不是只看「程式有沒有丟例外」。
