# JET 開發指南 (Journal Entry Testing — Single Source of Truth)

本文件是 JET 新系統唯一的深度參考。它涵蓋業務領域、規則規格、系統架構、資料策略、AI 協作方式與遷移計畫這幾個面向。

> **要理解這個系統，你只需要讀這份文件，不需要去讀 `legacy/ideascript.bas` 那 11,000 行原始碼。**
> 如果你發現本文件有歧義或缺口，請直接修正本文件，不要回頭去翻 VBA 程式碼。

---

## 目錄

- [A. 業務與領域](#a-業務與領域)
  - [1. JET 是什麼](#1-jet-是什麼)
  - [2. 核心資料實體](#2-核心資料實體)
  - [3. 審計工作流程](#3-審計工作流程)
  - [4. 資料驗證規則](#4-資料驗證規則)
  - [5. 預篩選規則聲明式規格](#5-預篩選規則聲明式規格)
  - [6. 進階篩選邏輯](#6-進階篩選邏輯)
  - [7. 審計工作底稿](#7-審計工作底稿)
  - [8. 台灣在地化](#8-台灣在地化)
- [B. 技術決策](#b-技術決策)
  - [9. 技術約束與排除選項](#9-技術約束與排除選項)
  - [10. 為什麼選 .NET 10 + WinForms + WebView2 + HTML](#10-為什麼選-net-10--winforms--webview2--html)
- [C. 系統架構](#c-系統架構)
  - [11. 架構總覽](#11-架構總覽)
  - [12. 層級職責](#12-層級職責)
  - [13. SQLite / SQL Server Provider 策略](#13-sqlite--sql-server-provider-策略)
  - [14. 專案結構規劃](#14-專案結構規劃)
  - [15. 命名與分層原則](#15-命名與分層原則)
- [D. 開發與協作](#d-開發與協作)
  - [16. AI-agent 開發工作流](#16-ai-agent-開發工作流)
  - [17. 從 ideascript.bas 遷移的做法](#17-從-ideascriptbas-遷移的做法)
  - [18. 欄位對照表](#18-欄位對照表)
  - [19. 術語對照](#19-術語對照)

---

# A. 業務與領域

## 1. JET 是什麼

日記帳分錄測試（Journal Entry Testing，JET）是一種實質性審計程序，依 ISA 240 與 ISA 330 兩號審計準則執行。它要因應的是「管理階層凌駕控制（Management Override of Controls）」這項舞弊風險，也就是管理階層繞過內部控制去操縱帳務。

實務上，JET 針對一個會計期間內的全部日記帳分錄，依風險導向的條件做全母體篩選，目的是辨識出高風險或異常的分錄，交給審計人員後續調查。

管理階層通常透過下列幾種方式操縱財務報表，這些正是 JET 要鎖定的風險面向：

- 記錄虛構或不當分錄（特別是期末結帳前後）
- 不當的估計調整
- 隱匿或延遲認列

一言以蔽之，JET 要做的事就是從數十萬筆分錄裡，把有風險的那些分錄篩出來。

---

## 1.5 資料量規模與處理原則 (Non-Negotiable)

JET 的所有設計決策都由母體規模驅動。本節列出的原則沒有妥協空間，因此用「不可協商（Non-Negotiable）」標示。違反這些原則的程式碼，不管寫得多漂亮，在真實案件的資料量下都會崩潰，所以必須拒絕合入。

### 1.5.1 規模假設

下表界定設計時必須涵蓋的資料量範圍。「典型」指常見的案件規模，「上界」指系統在設計上必須能承受的極端情形。

| 維度 | 典型 | 上界 (必須能跑) |
|:---|:---|:---|
| GL rows | 約 **1,000 萬** 以下的 local persistent case | **10 億** rows 的 large-data / cloud case 設計上限 |
| TB accounts | 約 **1,000** | **1 萬** |
| AccountMapping rows | 數百 ～ 數千 | 1 萬 |
| SQLite path | 小於 1,000 萬 GL rows 的本機持久案件 | 不作為 10 億 row 執行引擎 |
| SQL Server path | 大於 1,000 萬 GL rows 或 cloud / shared data case | 10 億 row 等級的 set-based execution target，需採分析型索引策略 |
| Workpaper Excel 大小 | 數 MB ～ 數百 MB | 需以 OpenXML SAX writer 控制記憶體 |

ISA 240 要求對全母體執行 JET，不允許用抽樣來取代規則篩選。因此資料驗證、預篩選與自訂 filter 全部都必須以全母體為計算基底。

### 1.5.2 唯一允許的計算位置：資料庫引擎（Set-Based Pushdown）

規則計算只能在資料庫引擎內進行，也就是把計算「下推（pushdown）」到 SQL，以集合運算（set-based）一次處理整批資料。禁止在 Application 層（C# / LINQ）對 GL/TB 的資料列集合執行驗證（Validation）、預篩選（Rule）或進階篩選（Filter）規則，以下統稱 V/R/Filter 規則。所有規則都必須寫成 SQL，由 SQLite 或 SQL Server 引擎執行。

這樣規定有三個理由：
1. **記憶體**：1,000 萬筆 GL 列乘上 12 欄、平均每欄 50 byte，本身就已經是數 GB 等級。再加上 C# Dictionary 的額外 overhead，記憶體用量還會是 2 到 3 倍。把這些載進 Application 程序會直接 OOM（記憶體耗盡）。10 億列等級更只能交給資料庫引擎，搭配分頁與匯出管線處理。
2. **效能**：資料庫引擎的 hash join、index scan、parallel aggregation 比手寫 LINQ 快 1 到 3 個數量級。
3. **可重現**：SQL 規則可以單獨拿到資料庫工具裡重跑驗證，C# LINQ 規則做不到這點。

**正確的寫法**（對應 §13 的設計）：

```csharp
public interface IGlRepository
{
    Task<RuleResult> RunValidationAsync(ProjectId id, ValidationKind kind, CancellationToken ct);
    Task<RuleResult> RunPrescreenAsync(ProjectId id, RuleSpec rule, CancellationToken ct);
    Task<FilterResult> RunFilterAsync(ProjectId id, ScenarioSpec scenario, CancellationToken ct);
}
```

Repository 內部會生成 SQL，例如借貸不平測試會產生這樣的查詢：

```sql
SELECT doc_num, SUM(amount_scaled) AS net_scaled
FROM target_gl_entry
WHERE project_id = @projectId
GROUP BY doc_num
HAVING ABS(SUM(amount_scaled)) > 0;
```

**禁止的寫法**：

```csharp
var v2NullDocNums = gl.Count(r => string.IsNullOrWhiteSpace(GetGlVal(r, "docNum", mapping)));
// GL rows 被載入 Application 記憶體後以 LINQ 計算 → 規模禁忌
```

### 1.5.3 金額精度：資料庫計算一律使用 Scaled Integer

JET 的金額計算屬於審計證據，不接受浮點近似。問題在於 SQLite 沒有原生的 `decimal` storage class。如果把金額存成 `REAL`，`SUM` 與 `ABS(diff)` 這類運算可能出現浮點誤差。如果改用文字保存 decimal，跨 provider 聚合的語意又不穩定。

為了避開這兩個問題，資料庫的權威計算欄位一律使用 `BIGINT` 的縮放整數（scaled integer），也就是把金額乘上固定倍率後以整數儲存：

| 欄位 | 型別 | 說明 |
|:---|:---|:---|
| `AmountScaled` | `BIGINT` | GL 標準金額，正借負貸 |
| `DebitAmountScaled` | `BIGINT` | 借方金額，非負 |
| `CreditAmountScaled` | `BIGINT` | 貸方金額，非負或依 schema 統一為非負 |
| `ChangeAmountScaled` | `BIGINT` | TB 期間變動額 |
| `MoneyScale` | `INTEGER` | 專案層級金額 scale，例如 `10000` |

規則如下：

1. 匯入時先把來源金額轉成 `decimal` 驗證，再乘以固定的 `MoneyScale`。所採用的四捨五入策略必須寫入 project config。
2. SQLite 與 SQL Server 的 V/R/Filter SQL 只對 scaled integer 做 `SUM`、`ABS`、比較與取模運算。
3. 前端、報表與工作底稿要顯示金額時，再除以 `MoneyScale` 還原。
4. 所有 provider 必須用同一組 fixture 驗證 scaled 結果一致。

連續零尾數測試也應該用整數取模來判斷，不要用 `RIGHT` 或 `REPEAT` 這類各 provider 方言差異很大的字串函式。

### 1.5.4 Bridge 不得搬運完整資料列集合

WebView2 與 .NET 之間的 `postMessage` 通道是把 JSON 當字串傳遞的。如果對 100 萬列、數十欄的資料做 `JSON.stringify`，會出現三個問題：

- JS 端記憶體耗盡（OOM）。
- 序列化本身耗時超過 10 秒，並且阻塞 UI thread。
- 反序列化時 .NET 端還要再耗一次。

**規則**：

| 動作 | 正式契約要求 |
|:---|:---|
| `import.gl.fromFile` / `import.tb.fromFile` | `{ filePath, fileName?, mode? }`；handler 透過 file reader streaming 直入 DB；payload 不帶 rows |
| `validate.run` | 回 summary 數字 + `resultRef`；明細透過 `query.validationDetailsPage` 分頁拉 |
| `prescreen.run` | 回每條規則命中數 + `resultRef`；明細走 `query.prescreenPage` |
| `filter.preview` | 回 count、voucherCount、summary、`previewRows` 上限 1000、`resultRef` |
| `query.*Page` | 使用 keyset paging：`{ projectId, cursor?, pageSize? }` → `{ rows, nextCursor }` |

以資料列為單位的 demo 與 import action 只是歷史相容路徑，不得出現在正式 UI、測試資料管線或新程式碼中。

### 1.5.5 Excel 工作底稿採用 OpenXML SAX Writer

`export.workpaper` 預期會輸出多張工作表，明細層的總大小可達數百 MB。因此正式實作必須走 OpenXML 的 SAX writer（例如 `DocumentFormat.OpenXml` 的 `OpenXmlWriter`），大型明細列使用 inline string 寫出。禁止把整份 result set 先載入 `DataTable`、`List<>` 或 DOM workbook 之後再寫，否則同樣會撐爆記憶體。

ClosedXML 可以用來做小型 summary sheet 的實驗，但不得拿來當大資料量工作底稿的 writer。

### 1.5.6 Session State 只保存輕量指標

Session store 只能保存目前的 projectId、目前的 mappings、UI 暫態與最新的 resultRef。它不得持有 GL/TB 的資料列。GL/TB 的原始列一律落地到 `staging_*` 資料表，標準化後的資料落地到 `target_*`，規則結果落地到 `result_*`。

### 1.5.7 自我檢查清單

每次新增或修改 handler 時，請逐項自問下面幾題：

1. 我有沒有把任何 GL/TB 列集合載入 `List<>` 或 `Dictionary<>`，然後跑 LINQ？
2. 我有沒有讓 bridge 的 payload 或 response 攜帶超過 1000 列的明細？
3. 我有沒有建立 in-memory cache 去取代資料庫查詢？
4. 我的金額計算是否全程使用 scaled integer，而沒有用到 SQLite 的 `REAL` 或文字 decimal 聚合？
5. 我寫的 SQL 在 1,000 萬筆本機 GL、以及更大型的 SQL Server case 上，執行形狀合理嗎？也就是有沒有用 index、有沒有避免 `SELECT *`、有沒有用 keyset 分頁？
6. SQL Server 的大資料表，我有沒有評估過 columnstore 或 rowstore 輔助索引？
7. 我的 Excel 寫入是否使用 OpenXML SAX writer？

只要有任何一題違反上述的安全形狀，這個設計就需要重做。

---

## 2. 核心資料實體

整個 JET 系統只圍繞 5 個核心實體運作：總帳（General Ledger，GL）、試算表（Trial Balance，TB）、科目配對表（AccountMapping）、日期維度（DateDimension）與規則結果（RuleResult）。只要理解這五者的欄位與彼此的關係，就掌握了全系統的資料模型。

### 2.1 總帳分錄（GL，General Ledger）

每一筆 GL 代表一張傳票裡的一個分錄行，是 JET 主要的分析對象。

#### 必要欄位

| 標準欄位 | 型別 | 必填 | 說明 |
|:---|:---|:---|:---|
| `DocumentNumber` | string | ✅ | 傳票號碼 |
| `LineItem` | string |  | 同傳票內的分錄序號 |
| `Amount` | decimal | ✅ | 顯示 / DTO 用金額；DB 權威計算使用 `AmountScaled` |
| `AmountScaled` | long | ✅ | DB 標準金額，正借負貸，依 project `MoneyScale` 縮放 |
| `AccountCode` | string | ✅ | 會計科目編號 |
| `AccountName` | string |  | 會計科目名稱 |
| `DocumentDescription` | string |  | 傳票摘要 |
| `ApprovalDate` | date |  | 傳票核准日 (期末後核准、週末/假日核准規則必要) |
| `PostDate` | date |  | 總帳日期 |
| `VoucherDate` | date |  | 傳票日期 (選填；回溯過帳規則必要，比對過帳日是否早於傳票日) |
| `CreatedBy` | string |  | 編製人 (編製者彙總必要) |
| `ApprovedBy` | string |  | 核准人 |
| `SourceModule` | string |  | 來源子系統 (AP、AR、GL…) |
| `IsManual` | bool |  | 是否人工傳票 |

#### 衍生欄位 (匯入後計算)

| 欄位 | 計算 | 用途 |
|:---|:---|:---|
| `DebitAmountScaled` | `AmountScaled >= 0 ? AmountScaled : 0` | 編製者/罕用科目彙總 |
| `CreditAmountScaled` | `AmountScaled < 0 ? ABS(AmountScaled) : 0` | 編製者/罕用科目彙總 |
| `DrCr` | `AmountScaled >= 0 ? "DEBIT" : "CREDIT"` | 借貸方向 |

#### 金額模式（四選一）

不同 ERP 記錄金額的方式各不相同，因此匯入時由使用者指定採用哪一種模式：

| 模式 | 來源欄位 | 轉換為標準 `Amount` 的規則 |
|:---|:---|:---|
| `SignedAmount` | 單一金額欄 | 直接使用 (正=借、負=貸) |
| `AmountWithSide` | 絕對值 + 借貸別欄 | 借貸別 = "D" 取正，"C" 取負 |
| `AmountWithFlag` | 絕對值 + 借方標誌 (0/1) | flag=1 取正，flag=0 取負 |
| `DualAmount` | 借方金額 + 貸方金額 | `Amount = Debit - Credit` |

### 2.2 試算表（TB，Trial Balance）

試算表是各會計科目在會計期間內的餘額彙總，用於完整性測試（`completeness_test`）。

| 標準欄位 | 型別 | 說明 |
|:---|:---|:---|
| `AccountCode` | string | 科目代號 |
| `AccountName` | string | 科目名稱 |
| `ChangeAmount` | decimal | 顯示 / DTO 用期間淨變動額 |
| `ChangeAmountScaled` | long | DB 權威計算用期間淨變動額 |
| `OpeningBalance` / `ClosingBalance` | decimal | 顯示 / DTO 用期初 / 期末 |
| `OpeningBalanceScaled` / `ClosingBalanceScaled` | long | DB 權威計算用期初 / 期末 |
| `OpeningDebitBalanceScaled` / `OpeningCreditBalanceScaled` | long | DB 權威計算用期初借 / 期初貸 |
| `ClosingDebitBalanceScaled` / `ClosingCreditBalanceScaled` | long | DB 權威計算用期末借 / 期末貸 |
| `DebitAmountScaled` / `CreditAmountScaled` | long | DB 權威計算用本期借方 / 貸方合計 |

#### TB 變動金額計算模式（匯入時決定）

| 模式 | 可用欄位 | `ChangeAmount` 計算 |
|:---|:---|:---|
| `DirectChange` | 變動金額 | 直接採用 |
| `OpenClose` | 期初 + 期末 | `Closing - Opening` |
| `DebitCredit` | 借方 + 貸方 | `Debit - Credit` |
| `OpenCloseBySide` | 期初借貸 + 期末借貸 | `(ClosingDr - ClosingCr) - (OpeningDr - OpeningCr)` |

> **領域背景：為何 TB 比對的是「本期變動」而非餘額。** 會計科目編號開頭的碼段決定了這個科目的時間語義。開頭是 `1` 到 `3` 的屬於資產、負債、權益，是資產負債表科目，有期初與期末的累計餘額概念，它的本期變動等於期末減期初。開頭是 `4` 到 `7` 的屬於收入、費用之類的損益表科目，記的是當期數值、每期歸零，因此當期數本身就是本期變動。
> 這正是上表 `OpenClose` 與 `OpenCloseBySide` 這兩個模式存在的理由。它們把資產負債表科目的期初與期末推算成「本期變動」，讓這些科目能和 GL 的本期借貸彙總落在同一個基礎上比對（這就是完整性測試，見 §4）。損益表科目的當期數則可以直接當成本期變動使用。
> 這一段講的是領域語義。要注意的是，變動模式是在匯入時決定的（見上表），不是規則程式裡依科目碼做的分支。

### 2.3 科目配對表（AccountMapping）

科目配對表把企業自己的科目對應到一組標準化分類，用於未預期借貸組合規則與科目配對分析（見 §6.1）。

| 欄位 | 說明 |
|:---|:---|
| `AccountCode` | GL 科目代號 |
| `AccountName` | 科目名稱 |
| `StandardizedCategory` | `Revenue` / `Receivables` / `Cash` / `Receipt in advance` / `Others` |

### 2.4 日期維度（DateDimension）

日期維度是審計期間內每一天的屬性表，用於週末與假日的過帳、核准規則。

| 欄位 | 說明 |
|:---|:---|
| `DateKey` | YYYYMMDD |
| `FullDate` | 日期 |
| `DayOfWeek` | 1=日 … 7=六 |
| `IsWeekend` | 系統自動 (Sat / Sun) |
| `IsHoliday` | 由使用者上傳的假日曆覆寫 |
| `IsMakeupDay` | 補班日 (假日曆的例外) |
| `HolidayDesc` / `MakeupDayDesc` | 說明 |

假日與補班日的名稱（`HolidayDesc` 與 `MakeupDayDesc`）存放在 `staging_calendar_raw_day.day_name`，由行事曆檔案匯入時一併帶入（見 §3.1）。如果走的是以 `dates` 陣列匯入的相容路徑，名稱會是 null。

### 2.5 規則結果（RuleResult）

規則結果記錄每一條規則執行後產生的標記與狀態。

| 欄位 | 型別 | 說明 |
|:---|:---|:---|
| `RuleSlug` | string | 命名登錄表的 slug（例 `post_period_approval`，見 §4） |
| `DocumentNumber` | string | 被標記的傳票 |
| `LineItem` | string | 被標記的分錄行 (可為空，表示整張傳票) |
| `Status` | enum | `V` (有結果) / `N/A` (無結果或未執行) |
| `TagColumn` | string | 結果標記欄名，由 slug 推導 (例 `tag_post_period_approval`) |

**結果失效不變量（衍生資料，不可協商）**：規則結果（`result_rule_run` 與 `result_inf_sampling_test_sample`）是依當前的 `target_*`、科目配對與行事曆算出來的衍生資料，因此它必須永遠對應到當前的資料。

有幾種操作會改寫這些上游資料：GL/TB 重新匯入、重新提交配對（這兩者都會觸發重投影）、科目配對匯入、行事曆匯入。這些操作必須在改寫資料的同一個交易內一併清除規則結果，這樣「資料已經換了、舊結果卻還在」的中間態才不可能出現。反過來說，如果上游交易 rollback，清除動作也會跟著回退，所以也不會出現「結果已清掉、但資料其實沒換」的反向半態。清除之後 `project.load.latestRuns.*` 會回 null，前端據此顯示「未執行」並要求重跑。

實作上只有一個事實來源：在各個上游交易內呼叫 `RuleRunResultReset.ClearWithinAsync`。日後只要新增任何會改寫上游 target 的路徑，都必須一併接上這個清除動作。

`source_row_number` 是唯一一個可以跨重投影持久化的批次穩定鍵。`entry_id` 是 AUTOINCREMENT，重投影後會重新編號，所以不能拿來當跨重投影的持久參照。正因如此，INF 抽樣才改以 `source_row_number` 排序（見 §4）。

### 2.6 三層表名登錄（Domain 的單一事實來源）

JET 的審計方法學承襲自一套 legacy 的 Excel-VBA 工具。那套工具的資料表是一個乾淨的三層結構，分別是來源層（Source，受查者提供的資料原貌）、暫存層（ETL-Staging，標準化後的測試母體）與報表層（Target-Report，可判讀的結果），並且以審計詞彙命名，例如 `JE_PBC`、`JE`、`ACCOUNT_MAPPING` 等。現行系統的實體資料表則改用工程詞彙命名，例如 `staging_gl_raw_row`、`target_gl_entry` 等。

為了讓審計人員看到熟悉的審計名稱、同時又不冒任何審計邏輯上的風險，本系統採用一種「只加不改」的 metadata 模型。實體表名維持不變，另外建立一份中央表名登錄，把每張實體表對應到它的正準審計名、所屬層、曝光程度與一句中文說明。這份登錄是程式內的單一事實來源，放在 Domain 的 `JetSchemaCatalog`。它不依賴任何框架、不做 I/O，只承載純審計詞彙與 metadata。資料預覽日後會改以登錄的正準名呈現，不過那是後續任務，本節只描述登錄本身。

**為什麼不直接把實體表改名（一個顯式的取捨，由使用者拍板）。** 全庫約有 341 處 inline SQL 直接以實體表名查詢。如果真的去改實體表名，等於要改掉全部這些 SQL、加上一次 schema migration、再重跑整套驗證，對已經穩定的審計計算來說風險很高。改用「登錄才是事實來源、實體表名一律不動、不加 migration、不改任何 inline SQL」這個只加不改的模型後，審計邏輯的風險為零。代價是多了一層名稱對照，但這層對照由 `JetSchemaCatalog` 集中吸收，呼叫端只透過一組窄查詢介面（`All`、`ByAudience`、`ByLayer`、`ResolveCanonical`、`TryGet`）取用，不需要知道對照的細節。

**層（Layer）表達的是審計語義，不是實體表的前綴。** 登錄裡的「層」描述的是資料在審計流程中扮演的角色，依序是來源、暫存、報表、系統。這跟實體表名的前綴（`staging_`、`target_`、`result_`、`config_`，見 §13 的「Schema 分層」）是兩條不同的軸，兩者不能混為一談。舉例來說，`target_account_mapping` 的實體前綴是 `target`，但在審計語義上它其實是 Source，因為科目對照是受查者提供的。同樣地，`staging_calendar_raw_day` 的實體前綴是 `staging`，但審計語義上它也是 Source，因為假日曆也是受查者提供的。登錄是依正準名來分層，而不是依實體前綴。

**曝光程度（Audience）** 決定資料預覽與結構總覽要如何呈現某張表：

| 曝光 | 意義 |
|:---|:---|
| `DataView` | 可逐列瀏覽，且出現在結構總覽（審計工作流程會直接接觸的資料） |
| `StructureOnly` | 出現在結構總覽，但此處不開放逐列瀏覽（結果與組態，語義以摘要或專用頁呈現） |
| `Hidden` | 純系統或 ETL 暫存 scratch，完全不對外曝光 |

下表是「實體表 → 正準名 → 層 → 曝光」的完整對照，涵蓋專案資料庫全部 19 張實體表，以 Infrastructure `SchemaSql` 的 `CREATE TABLE` 清單為準逐一登錄：

| 實體表名 | 正準審計名 | 層 | 曝光 | 說明 |
|:---|:---|:---|:---|:---|
| `staging_gl_raw_row` | `JE_PBC` | Source | DataView | 匯入原貌 GL（受查者提供，未標準化） |
| `staging_tb_raw_row` | `TB_PBC` | Source | DataView | 匯入原貌 TB（受查者提供，未標準化） |
| `target_account_mapping` | `ACCOUNT_MAPPING` | Source | DataView | 科目 → 標準化分類對照 |
| `target_authorized_preparer` | `AUTHORIZED_PREPARER` | Source | DataView | 授權編製人員清單 |
| `staging_calendar_raw_day` | `DATE_DIMENSION` | Source | DataView | 日期維度：假日 / 補班日 |
| `target_gl_entry` | `JE` | Staging | DataView | 標準化分錄（JET 測試母體） |
| `target_tb_balance` | `TB` | Staging | DataView | 標準化試算表餘額（本期變動額） |
| `result_rule_run` | `VALIDATION_OVERVIEW` | Target | StructureOnly | 資料驗證 / 預篩選結果摘要 |
| `result_filter_run` | `FILTER_HITS` | Target | StructureOnly | 進階篩選命中的行層落地 |
| `result_inf_sampling_test_sample` | `INF_SAMPLE` | Target | StructureOnly | INF 抽樣抽中的分錄樣本 |
| `config_field_mapping` | `FIELD_MAPPING_INFO` | System | StructureOnly | 已提交的欄位對應（GL / TB 各一列） |
| `config_filter_scenario` | `FILTER_CRITERIA` | System | StructureOnly | 使用者著作的進階篩選情境（條件樹） |
| `import_batch` | `IMPORT_BATCH` | System | StructureOnly | 匯入批次（每個資料集一筆） |
| `import_batch_source` | `IMPORT_BATCH_SOURCE` | System | StructureOnly | 匯入批次的多來源明細 |
| `gl_control_total` | `GL_CONTROL_TOTAL` | System | Hidden | 完整性 part(a) 控制總數（中間計算） |
| `app_message_log` | `APP_MESSAGE_LOG` | System | Hidden | 前端狀態與訊息（UX 輔助，非審計留痕） |
| `schema_info` | `SCHEMA_INFO` | System | Hidden | schema 版本（遷移鏈判斷用） |
| `staging_account_mapping_raw_row` | `ACCOUNT_MAPPING_PBC` | Staging | Hidden | 科目配對匯入原貌暫存（ETL scratch） |
| `staging_authorized_preparer_raw_row` | `AUTHORIZED_PREPARER_PBC` | Staging | Hidden | 授權編製人員匯入原貌暫存（ETL scratch） |

**計算檢視（衍生資料，不落地成表）。** legacy 的 `COMPLETENESS_CALCULATED`、`COMPLETENESS_DIFF`、`COMPLETENESS_DETAIL`、`JE_IN_PERIOD`、`JE_NOT_IN_PERIOD`，以及各個 `*_OVERVIEW`，在本系統都是查詢當下才即時算出來的計算檢視。完整性是在規則 SQL 內以 CTE 計算，期間切分則在 query 層處理。它們不是實體資料表，所以不列入登錄，也不該為它們臆造對應的資料表。

**漂移守門。** 登錄與真實 schema 是否一致，由兩道測試把關。第一道是 Domain 單元測試，負責鎖住結構不變量（實體名與正準名都唯一、每一筆都有層與曝光、曝光表的正準名非空），並逐表比對決策（physical 對應到 canonical、layer、audience，只要改動任一項測試就轉紅）。第二道是 Infrastructure 測試，它用真的 SQLite 建一個全新的專案庫，查 `sqlite_master` 取出全部使用者表，再跟登錄做雙向比對。這道測試的用意是：日後如果新增了實體表卻忘了登錄（漏登錄），或登錄了一張其實不存在的表（幽靈條目），測試都會轉紅。

---

## 3. 審計工作流程

JET 的審計作業由五個循序步驟組成，從資料匯入一路推進到工作底稿產出：

```
Step 1  資料匯入與驗證   →  GL/TB → 標準化 → 四項資料驗證
Step 2  輔助檔案設定     →  AccountMapping / Holiday / MakeupDay → DateDimension
Step 3  預篩選           →  預篩選規則 (全母體打標)
Step 4  進階篩選         →  組合 tag + 自訂條件 → 最終測試母體
Step 5  工作底稿產出     →  Excel 多工作表
```

每個步驟的輸入、處理與產出已併入各節說明。

> **審計方法論對照（這套流程在服務什麼）。** 上述工具流程服務的是一套與 ISA 對齊的會計分錄測試方法論。依審計實務，最終的工作底稿（也就是 Step 5 的產出）分成五段：① 母體完整性、借貸不平與編製者彙總；② 攸關資料元素（Relevant Data Elements，RDE）可靠性測試（見 §4）；③ 高風險範圍條件彙總；④ 符合高風險條件之分錄測試；⑤ 財務報表關帳後的調整分錄。這五段最後會納入 eAudIT。工具的「匯入 → 驗證 → 預篩選 → 進階篩選 → 匯出」這條流程，產出的就是這五段底稿所需的資料。
> **狀態：工作底稿匯出（Step 5）的後端 writer 已經實作完成，正等待 GUI 驗收。** 這是子專案 E1（工作底稿匯出 writer，以 OpenXML SAX 串流寫出 15 張條件工作表）的成果，詳見 §7.2；前端的現況詳見 `docs/jet-frontend-description.md`。這五段底稿的詳細分頁版面是設計目標。完整且已清洗過的版面，以及它的歷史實作，請參見 `legacy/jet-legacy-notes.md`。

### 3.1 匯入格式與案件設定

正式匯入格式支援 `.xlsx`、`.csv` 與 `.txt`，其中 `.txt` 的內容一律當成 CSV 文字處理。若未來要新增格式，必須先更新本文件與 `docs/action-contract-manifest.md`，再去補對應的 reader 與 handler。

#### 3.1.1 文字檔（.csv / .txt）讀取規則

- **編碼偵測**：採確定性的判斷鏈，不做啟發式猜測。先看 BOM（可分辨 UTF-8、UTF-16 LE/BE）。若沒有 BOM，就用嚴格的 UTF-8 解碼去驗證取樣段，能通過就判定為 UTF-8。再不行就視為 Big5（CP950）。如果連 Big5 都解不開，回報 `file_read_error`，並建議使用者把來源另存成 UTF-8。匯入參數也可以明確指定編碼（`utf-8`、`big5`、`utf-16`）來覆寫自動偵測。
- **分隔符偵測**：候選的分隔符有 `,`、Tab、`;`、`|`。做法是以引號感知（quote-aware）的方式取樣前面若干個邏輯列，統計每個候選在引號外出現的次數，取各列都一致的那一個。如果有多個候選並列，就依固定優先序 `,` > Tab > `;` > `|` 取捨。如果統計結果全為 0，就判定為單欄檔，這是合法的。匯入參數同樣可以覆寫。
- **引號**：依 RFC 4180 處理。被雙引號包覆的欄位內，分隔符與換行都不切欄，而 `""` 會跳脫成單一個 `"`。這正是含千分位逗號的金額在 CSV 裡不會錯位的關鍵保證。
- **標頭正規化**：與 `.xlsx` 共用同一套規則，包含 trim、把空白標頭改成 `COL_{n}`、把重複的標頭加上 `_2` 或 `_3` 字尾。這樣做是為了確保到了 mapping 階段，欄名能一字不差地對得上。

#### 3.1.2 金額欄位接受格式

匯入投影階段以 InvariantCulture 的 `decimal` 解析金額。這裡採寬鬆策略，是為了對齊審計員會先行做初步清理的工作習慣。具體規則如下：

- 接受千分位逗號（`4,353,170.00`）、前置正負號（`-50`、`+50`）與小數點。
- 接受會計格式的零。也就是 trim 後恰好只剩一個半形連字號 `-` 時，視為 0。這是 Excel 會計數字格式（內建的 numFmtId 43 等，格式碼為 `_-* #,##0.00_-;…;_-* "-"??_-`）對零的標準顯示方式，真實 PBC 提供的 TB 匯出普遍都長這樣。要注意只有這一個字元成立，全形的 `－`、em-dash `—`、`--`、`-.`、`- 0` 一律仍然拒絕。
- 不處理貨幣記號（`NT$`、`$`、`元` 等）、括號負數、全形數字、歐陸格式（`1.234,56`）。遇到這些就回報投影錯誤，並逐列指出是哪個欄位、原值是什麼。這裡的前提是期待來源資料已先完成初步清理。
- 解析成功後，金額會乘上專案的 `MoneyScale` 轉成 scaled integer（見 §1.5.3）。權威計算只在 scaled BIGINT 上進行。

#### 3.1.3 日期欄位接受格式

日期的判定順序是固定的，先到先得；不論命中哪一條，輸出一律是 `yyyy-MM-dd` 字串：

1. ISO 格式 `yyyy-MM-dd`。
2. 顯式的西元格式：`yyyy/M/d`、`yyyy.M.d`、8 位數的 `yyyyMMdd`。
3. 民國年（由專案設定 `rocDateEnabled` 控制，預設啟用）。涵蓋兩種寫法：3 位數年加上 `/` 或 `.` 分隔（例如 `114/6/11`），以及 7 位數的 `1140611`（格式為 `1yy MMdd`）。判定時把民國年加上 1911 轉成西元。要注意 7 位數的民國年和 Excel 日期序列值的數字範圍會重疊，此時民國年判定優先於序列值。如果關閉了 `rocDateEnabled`，7 位數就回歸序列值判定。
4. Excel 日期序列值，範圍 `1` 到 `2958465`（OADate）。
5. 寬鬆 fallback，使用 InvariantCulture 的 `DateTime.TryParse`。但解析出來的年份必須落在 1900 到 2100 之間，否則視為解析失敗。這條限制是為了避免 `114/6/11` 在民國年關閉時，被默默當成西元 0114 年吞掉。
6. 兩位數年（例如 `11/05/06`）因為無法分辨到底是民國年、西元年，還是日月的順序，所以一律拒絕，並回報投影錯誤。

#### 3.1.4 多來源合併（一個資料集可由多個檔案／工作表組成）

實務上，一個年度的 GL 可能拆成多份交付，例如同一個活頁簿裡的 Q1 到 Q4 季別工作表、逐月的 CSV，或好幾個各只有一張表的 `.xlsx`。為了處理這種情形，匯入模型維持一條不變式：一個資料集對應一個匯入批次。一個批次可以由一到多個來源（source）組成，規則如下：

- 第一個來源以 `mode:"replace"` 開立批次，後續的來源則以 `mode:"append"` 加入同一個批次。每個來源都記錄在 `import_batch_source` 裡（包含檔名、工作表、編碼、分隔符、列數、匯入時間），供介面顯示與審計留痕。
- 所有來源的有效欄位集合必須一致（與順序無關；有效欄位的定義見 §3.1.5 的收斂規則）。只要不一致就拒絕，回報 `column_mismatch`。這是因為欄位配對是批次層級的概念，不允許出現「半個資料集換了欄位結構」這種情況。
- 附加時的欄位驗證分成兩階段。第一階段在串流寫入之前，先比對雙方的具名標頭集合，目的是快速失敗，避免白白讀完一個大檔。第二階段在串流完成之後，再用收斂後的有效欄位集合做終檢，這一步能涵蓋「沒有標頭但帶資料的欄」這種情形，確保有資料的欄位永遠不會被靜默丟掉。如果終檢不符，就 rollback 這次來源，既有的批次完全不受影響。
- 附加和 replace 一樣會讓下游失效，也就是會清除 target 投影與已提交的配對。原因是母體變了，配對與規則結果都必須重做。
- 暫存列保有兩個不同的列號概念。`source_row_number` 是來源檔案內的實際列號，作用是讓投影錯誤訊息能指回使用者在檔案裡看得到的那一列。`row_number` 則是批次內單調遞增的排序鍵，是 INF 抽樣穩定性的基礎；附加來源時，它會從既有的最大值繼續往下編。

#### 3.1.5 Excel（.xlsx）讀取規則與欄位集合收斂

`.xlsx` 一律以 OpenXML 的 SAX 串流方式讀取，也就是 forward-only、不建立 worksheet 的 DOM。系統只有單一一個讀取器，不會依檔案大小分支：百萬列等級的真實 PBC 活頁簿和小檔走的是同一條路徑（實際驗證過 114MB、兩張工作表合計 1,403,327 列的案例）。檔案檢視（inspect）與讀取欄名都只讀到標頭列就停，所以檢視的回應時間和檔案大小無關。

讀取行為如下（標頭正規化與 CSV 讀取器共用同一套，因此 staging 的輸出形狀一字不差）：

- **標頭列**指的是第一個含有非空萃取值的列；只有樣式、沒有值的列不算。cell 依實際欄號對應。OOXML 容許省略列號或 cell 參考（`r` 屬性），缺席時就以連續計數遞補。
- **共用字串**：`sharedStrings.xml` 會一次串流載入，只取 `<t>` 與 rich text run `<r><t>` 的文字。要特別排除 `<rPh>`（注音讀音 run）與 `<phoneticPr>`，因為台灣 ERP 匯出的活頁簿常帶注音標記，這些東西混進來會污染欄名與摘要。
- **cell 型別**：`t="s"` 是共用字串、`t="inlineStr"`、`t="str"` 是公式的字串結果、`t="b"` 解析成 `true`/`false`、`t="e"` 取錯誤原文；沒有型別屬性就當數值。公式 cell 取它的快取值 `<v>`；如果沒有快取值就視為空，因為串流讀取不會去重算公式。
- **數值正規化**：先把 `<v>` 以 `double` 解析（容許科學記號），接著轉成 `(decimal)`，再轉成 InvariantCulture 字串（如果超出 decimal 範圍，就退回 round-trip 的 `"R"` 格式）。這裡 double 轉 decimal 的步驟會吸收浮點殘影，例如把 `535.04999999999995` 修正成 `"535.05"`，這是 staging 顯示值與金額解析能保持穩定的關鍵。
- **日期樣式判定**：`styles.xml` 會一次解析。如果一個數值 cell 的樣式 numFmtId 屬於日期格式，就用 `FromOADate` 轉成 `yyyy-MM-dd`。判定日期格式的依據是：numFmtId 落在內建日期 id 集合內，或自訂格式碼在「引號字面值、`[...]` 區段、反斜線跳脫」之外含有 y/m/d 記號。若格式只含 h/m/s 則視為時間值。要注意型別優先於樣式，也就是共用字串 cell 即使套了日期樣式仍然是字串。`date1904` 活頁簿的序列值要 +1462 校正。至於沒有套日期樣式的日期序列值，留待投影階段由 `DateNormalizer` 以 OADate 規則兜底處理（對應 §3.1.3 的第 4 順位）。
- 只有樣式、沒有值的 cell（`<c s="2"/>`）視為空；全空的列直接跳過；`SourceRowNumber` 取工作表內的實際列號。
- 工作表以名稱解析，不分大小寫；沒指定時取第一張。chartsheet 不算資料工作表，會從清單裡剔除。

**欄位集合收斂（決定批次的有效欄位）**：staging 是以欄名當鍵的，所以「一個欄位到底算不算存在」必須有一個跨來源都穩定的定義。規則是這樣：

- 具名標頭一律算批次欄位，即使整欄都沒有資料也一樣。原因是具名的空欄本身就是來源 schema 的一種聲明。
- 至於空白標頭產生的 `COL_{n}` 佔位欄，只有在串流過程中實際觀察到至少 1 個非空值時，才算批次欄位。標頭列縫隙產生的佔位欄是這樣，標頭範圍之外冒出資料 cell 時 lazy 合成的佔位欄也是這樣。後者保證了「有資料的欄絕不會被靜默丟棄」，而且不依賴 `<dimension>` 元素是否正確。
- 收斂這個動作，在倉儲串流寫入結束之後、於同一個交易內完成，並把結果回寫到批次的 `columns_json`。真實的動機案例是這樣的：同一個活頁簿裡，「上半年」表在 S、U 兩欄之間夾了一個空標頭、整欄無資料的 T 欄（佔位欄 `COL_20`），而「下半年」表沒有這一欄。由於兩張表的具名標頭集合完全相同，收斂之後就能正常合併成一個批次。反過來說，如果 T 欄其實有資料，終檢就會誠實地回報 `column_mismatch`，而不是默默把欄丟掉。
- 收斂是一個 provider 中立的純函式（Domain 的 `TabularHeaderNormalizer.FinalizeBatchColumns`），SQL Server 與 DuckDB 的倉儲實作可以直接重用。本規則生效之前建立的批次，其 `columns_json` 可能還殘留著無資料的佔位欄，但這不需要做資料遷移，因為對一個全空的欄做配對本來就沒有意義。

#### 3.1.6 行事曆檔案匯入（事務所假日／補班表）

事務所的行事曆表（假日表、補班與結帳日表）以 `import.holiday.fromFile` 與 `import.makeupDay.fromFile` 匯入，只支援 `.xlsx`（範本帶有樣式標題列；非 `.xlsx` 的檔案會回報 `unsupported_file_type`）。和一般匯入不同的是，這類範本的標頭固定在第 2 列，第 1 列是樣式標題，因此後端以 reader 的 `LeadingRowsToSkip=1` 略過第 1 列。

- 欄位辨識沿用既有的關鍵字命中機制。日期欄（`Date_of_Holiday` 或 `Date_of_MakeUpday`）是必要的，缺了就回報 `projection_failed`。名稱欄（`Holiday_Name` 或 `MakeUpDay_Desc`）與 `IS_Holiday` 則是選用的。假日表在 `IS_Holiday` 欄存在時只收值為 `Y` 的列，缺這個欄則全收；補班表沒有這層過濾。
- 多年度的資料一律照單全收（不依檔名的年度做過濾），同一天的資料會去重。只要有任一資料列的日期不是 `yyyy-MM-dd`，就回報 `projection_failed`（列出前 10 筆，且整批不寫入）。寫入 `staging_calendar_raw_day`（含 `day_name`）採 replace 語意，並且在同一個交易內清除規則結果（對應 §2.5 的不變量）。完整契約見 `docs/action-contract-manifest.md`。

#### 3.1.7 授權編製人員清單匯入（子專案 C，授權編製人員清單與相關規則）

授權編製人員清單是查核團隊維護的一份單欄姓名名單，提供給「非授權編製人員」規則（見 §5，歷史代號 R10）拿來比對 `created_by`。它以 `import.authorizedPreparer.fromFile` 匯入，只支援 `.xlsx`（單欄、英文標頭，例如 `AUTHORIZED_PREPARER`）。

- **解析**：沿用既有的 OpenXML SAX 讀取器與關鍵字命中的欄位解析方式（與科目配對、行事曆匯入是同一套範式），只認姓名欄，空白列會略過，姓名以 `TRIM` 正規化後再存。
- **語意**：採 replace-only，也就是重新匯入時會整份換掉，而且匯入即投影。它不寫 `import_batch`，因為這不是 GL/TB 母體匯入，不需要批次的概念。
- **schema**：兩個 provider 都建表，以 `IF NOT EXISTS`（SQLite）或 `IF OBJECT_ID`（SQL Server）建立，且不升 schema 版本，沿用 `app_message_log`、`gl_control_total`、`result_filter_run` 的先例。涉及的表有 `staging_authorized_preparer_raw_row`（staging 層）與 `target_authorized_preparer(name PRIMARY KEY)`（去重、TRIM 後儲存）。
- **失效**：重新匯入時，依賴這份名單的 R10 規則結果必須一併失效。做法是讓授權清單的 replace 與 `RuleRunResultReset.ClearWithinAsync` 在同一個交易內執行，使相關的預篩選與進階篩選結果重算（對應 §2.5 的不變量）。
- **resume**：`project.load` 的 importState 會輸出 `authorizedPreparer.rowCount`（取自 store 的計數），供使用者重開案件續作時還原名單的匯入狀態。完整契約見 `docs/action-contract-manifest.md`。

案件設定（Case config）必須能重新載入案件參數，至少要保存下列幾項：

- 案件 metadata：客戶、期間、操作者、產業、報表準備基準日。
- 欄位配對（field mapping）：原始欄位到標準欄位的對應。
- 科目配對與分類設定（account pairing / classification settings）。
- 已保存的查詢與篩選情境（saved query / filter scenarios）。
- 可重新載入的案件參數（reloadable case parameters）：重跑驗證、預篩選、匯出所需的參數。

### 3.2 專案組態持久化

JET 需要保存使用者在單一專案內的操作脈絡，避免重新開啟應用程式後遺失前一次的輸入。這項能力屬於架構基礎，不等同於 GL/TB 的規則計算。

初期雛形只需要建立本機的專案組態持久化（project configuration persistence），要求如下：

- 保存專案 metadata、欄位配對草稿、流程狀態、使用者選項，以及未來可重跑所需的參數。
- 儲存位置由 Infrastructure provider 管理。前端不得成為唯一的狀態來源。
- 一開始先由 SQLite 承擔本機組態儲存的雛形。後續如果要新增 DuckDB 或其他 provider，仍應透過同一組 application 與 domain 的邊界來替換。
- `docs/development-status.md` 記錄開發現況與未決事項，`docs/development-log.md` 記錄跨 session 的決策脈絡。正式的領域規則與架構決策仍然回寫到本文件與 action manifest（整個文件體系見 `docs/README.md`）。

---

## 4. 資料驗證規則

四項資料驗證在 Step 1 執行，目的是在進入風險篩選之前，先確認資料母體是完整、平衡且可信的。只要任何一項驗證失敗，就代表後續測試所依據的基礎資料可能不可靠。

### 規則命名登錄表（Rule Naming Registry）

JET 的規則一律以具體名稱來識別，不使用 V/R/A 這類流水代號。不用流水代號是因為歷史代號跨世代會產生歧義：legacy 系統的 V1 到 V4 指的是空值計數、V5 是完整性測試，這跟後來重新編號過的 V1 到 V4 指涉的根本是不同的測試。命名公約如下：

- **wire key**（用於 JSON 屬性、filter 的 `prescreenKey`）採 lowerCamelCase。
- **slug**（用於資料表、命名追溯）採 snake_case。
- **UI 與工作底稿分頁**用中文名。
- 程式內的單一事實來源是 `Domain/RuleCatalog.cs`。

| slug | wire key | 中文名 | 類別 | 歷史代號 |
|:---|:---|:---|:---|:---|
| `completeness_test` | `completenessTest` | 完整性測試 | 資料驗證 | V1（legacy V5） |
| `doc_balance_test` | `docBalanceTest` | 借貸不平測試 | 資料驗證 | V2（legacy V6） |
| `inf_sampling_test` | `infSamplingTest` | INF 抽樣測試 | 資料驗證 | V3 |
| `null_records_test` | `nullRecordsTest` | 空值紀錄測試 | 資料驗證 | V4（legacy V1–V4） |
| `post_period_approval` | `postPeriodApproval` | 期末財報準備日後核准之分錄 | 預篩選（row tag） | R1 |
| `suspicious_keywords` | `suspiciousKeywords` | 分錄摘要出現特定描述 | 預篩選（row tag） | R2 |
| `unexpected_account_pair` | `unexpectedAccountPair` | 未預期出現之特定借貸組合 | 預篩選（row tag） | R3 |
| `trailing_zeros` | `trailingZeros` | 分錄金額中有連續零的尾數 | 預篩選（row tag） | R4 |
| `creator_summary` | `creatorSummary` | 依分錄編製者彙總 | 預篩選（彙總） | R5 |
| `rare_accounts` | `rareAccounts` | 較少使用之科目 | 預篩選（彙總） | R6 |
| `weekend_posting` / `weekend_approval` | `weekendPosting` / `weekendApproval` | 週末過帳／週末核准 | 預篩選（row tag） | R7 |
| `holiday_posting` / `holiday_approval` | `holidayPosting` / `holidayApproval` | 假日過帳／假日核准 | 預篩選（row tag） | R8 |
| `backdated_posting` | `backdatedPosting` | 回溯過帳（過帳日早於傳票日） | 預篩選（row tag） | R9 |
| `non_authorized_preparer` | `nonAuthorizedPreparer` | 非授權編製人員 | 預篩選（row tag） | R10 |
| `low_frequency_preparer` | `lowFrequencyPreparer` | 低頻編製者 | 預篩選（row tag） | R11 |
| `low_frequency_account` | `lowFrequencyAccount` | 低頻科目 | 預篩選（row tag） | R12 |
| `blank_description` | `blankDescription` | 摘要空白 | 預篩選（row tag） | （舊 wire 鍵 descNull） |
| `account_pair` | `accountPair`（filter type） | 科目配對分析 | 進階篩選條件 | A3＋§6.1 |
| `period_in_out` | `periodInOut`（filter type） | 期內／期外 | 進階篩選條件 | — |
| `custom_keywords` | `customKeywords`（filter type） | 自訂關鍵字 | 進階篩選條件 | A2 |
| `custom_trailing_zeros` | `customTrailingZeros`（filter type） | 自訂尾數位數 | 進階篩選條件 | A4 |
| `custom_preparer_entry_count` | `customPreparerEntryCount`（filter type） | 自訂編製人員張數 | 進階篩選條件 | — |
| `custom_account_entry_count` | `customAccountEntryCount`（filter type） | 自訂科目張數 | 進階篩選條件 | — |
| `revenue_debit_near_quarter_end` | `revenueDebitNearQuarterEnd`（filter type） | 季末前借記收入 | 進階篩選條件（KCT） | — |
| `revenue_without_normal_counterpart` | `revenueWithoutNormalCounterpart`（filter type） | 收入無一般對方科目 | 進階篩選條件（KCT） | — |
| `manual_revenue_entry` | `manualRevenueEntry`（filter type） | 收入之人工分錄 | 進階篩選條件（KCT） | — |
| `trailing_digits` | `trailingDigits`（filter type） | 特定金額尾數 | 進階篩選條件（KCT） | — |
| `preparer_equals_approver` | `preparerEqualsApprover`（filter type） | 編製與核准同一人 | 進階篩選條件（KCT） | — |

表中的「歷史代號」欄只是為了方便回查 legacy 文件與舊版工作底稿，它不得出現在 UI、wire contract、資料表名或新的文件敘述裡。

### 完整性測試（completeness_test）

| 項目 | 內容 |
|:---|:---|
| **目的** | 兩段核對。part(a)：匯入母體的列數與金額合計在投影後未增減（控制總數對帳）。part(b)：GL 按科目加總應等於 TB 期間變動額；不等則代表 GL 母體不完整 |
| **邏輯（part a）** | 投影（`mapping.commit`）時一次累計並落地兩端控制總數：來源端（staging 匯入列數 + 原始金額欄合計）與母體端（`target_gl_entry` 列數 + 借方總額 + 貸方總額，scaled）；核對列數須相等、金額合計須相等 |
| **邏輯（part b）** | `FULL OUTER JOIN` GL_Sum_ByAccount × TB ON AccountCode；差異 = `TB.ChangeAmountScaled - GL.AmountScaledSum` |
| **異常條件** | part(a)：列數不一致 OR 金額不一致；part(b)：`ABS(差異) > 0` |
| **產出** | part(a)：來源／投影列數、借方總額、貸方總額，與「列數一致」「金額一致」布林（總額層級，不需明細分頁）。part(b)：差異科目清單 (科目 / TB 金額 / GL 金額 / 差異)，差異列附 `notInTb` 記號 |

> **Not-in-TB 具名化。** 在 part(b) 的差異裡，有一支是「科目在 GL 出現過、但 TB 沒有對應科目」的情形，也就是 GL 加總非零、而 TB 變動為 0。對於這種差異列，會加上一個 `notInTb` 的布林記號。這只是一個字面值的記號，用途是讓查核員辨識並呈現在底稿上，它不是一個獨立的旗標欄。要完整列舉出全部的 Not-in-TB 科目，屬於分頁能力（跨子專案），本測試只負責逐列判斷並打上記號。

### 借貸不平測試（doc_balance_test）

| 項目 | 內容 |
|:---|:---|
| **目的** | 每張傳票借貸應平衡；不平衡可能為資料品質問題 |
| **邏輯** | 按 `DocumentNumber` 加總 `AmountScaled`，篩 `SUM <> 0` 的傳票，取出完整明細 |
| **產出** | 不平衡傳票 + 其所有分錄 |

除了回報不平衡傳票的筆數之外，本測試在投影後還會額外提供最多 50 張不平衡傳票的明細樣本，供 UI 展開檢視。要注意這份明細是衍生出來的顯示值，不得拿來當作任何規則計算或抽樣的依據。SQLite 與 SQL Server 兩個 provider 的產出是等價的。

### INF 抽樣測試（inf_sampling_test）

| 項目 | 內容 |
|:---|:---|
| **目的** | 隨機抽樣供人工驗證非財務欄位 (摘要、日期、科目名等) 可靠性（INF = Information Produced by the Entity） |
| **邏輯** | 以審計期間 GL 為母體，以 `(source_row_number × seed) mod 2147483647`（tiebreak `entry_id`）的確定性整數 hash 排序取 N 筆（預設 60，可設定；seed 預設 48271）。`source_row_number` 為跨重投影穩定鍵（`entry_id` 為 AUTOINCREMENT、重投影會重編，故不作主鍵），確保重投影後抽中的樣本一致。實作與 manifest `validate.run` 細節一致 |
| **產出** | 抽樣明細 |

INF 抽樣必須可重現。每次抽樣都要保存 seed、抽中的 key 與 runId。禁止使用不可重現的隨機函式，也禁止把 SQL Server 的 `TABLESAMPLE` 拿來當作正式審計抽樣的依據。

> **審計方法論背景：INF 抽樣對應 RDE 可靠性測試。** INF 抽樣是「攸關資料元素（Relevant Data Elements，RDE）可靠性測試」目前在審計上的實作形式。RDE 指的是被納入高風險篩選條件的那些欄位，分成兩類。財務類包括會計科目編號與名稱、借貸別、金額，這些欄位的可靠性大多在完整性測試比對 TB 時就已經取得。非財務類則包括過帳日期、摘要關鍵字、人工或自動的註記、特定的編製或核准人員，這些需要抽樣核對傳票附件才能確認。
> 依 ISA 240 與 ISA 330（KAEG）的要求，被納入篩選的 RDE 必須在執行高風險篩選之前先確認可靠性。但如果還停留在風險評估的預篩選（screening）階段，就不需要先確認。完整的逐欄可靠性核對程序（財務類為 A，非財務類為 B 到 G）屬於審計方法論的範疇，詳見 `legacy/jet-legacy-notes.md`。這裡的 INF 抽樣，是那套程序在工具內的可重現抽樣機制。

### 空值紀錄測試（null_records_test）

| 項目 | 內容 |
|:---|:---|
| **目的** | 找出關鍵欄位為空的分錄 |
| **邏輯** | 分別篩 `AccountCode IS NULL` / `DocumentNumber IS NULL` / `DocumentDescription IS NULL`；另含第四旗標「日期區間外」：以**核准日**判定 `ApprovalDate IS NOT NULL AND (ApprovalDate < PeriodStart OR ApprovalDate > PeriodEnd)`（2026-06-23 決策，對齊舊 JET 工具的「Approval date out of period」；用核准日 `approval_date`／配對 `docDate`，非過帳日。核准日未配對則此旗標不命中） |
| **產出** | 三類空值清單與計數，加「日期區間外（核准日離期）」計數與明細 |

除了回報各類空值的筆數之外，本測試在投影後還會額外提供最多 50 列命中的明細樣本，供 UI 展開檢視。每一列都用 `issues` 標明它命中了哪幾項檢查，因為同一列有可能同時命中多項。要注意這份明細是衍生出來的顯示值，不得拿來當作任何規則計算或抽樣的依據。SQLite 與 SQL Server 兩個 provider 的產出是等價的。

---

## 5. 預篩選規則聲明式規格

本節以聲明式規格描述每一條預篩選規則。所謂聲明式，是指這份規格本身就足以直接生成實作（Command Handler、Repository Query、SQL），完全不需要回頭去翻 `ideascript.bas`。規則的識別一律使用命名登錄表（§4）裡的 slug、wire key 與中文名。

每一條規格用到的欄位，意義如下：
- **Name**：規則的中文名稱。
- **Slug / Wire key**：命名登錄表裡的識別字。
- **Rationale**：這條規則的風險意義，也就是為什麼它是一個風險指標。
- **Preconditions**：執行前必須滿足的條件。若未滿足，則 `Status = N/A`。
- **Input**：資料來源。
- **Predicate / Aggregation**：核心邏輯，以類 SQL 的偽碼表示。
- **Output**：回傳結果的形式，是 tag 還是彙總。
- **N/A When**：明確會判定為 N/A 的條件。
- **Work Paper Sheet**：匯出工作底稿時對應的分頁名，也就是中文的規則名。

### 期末財報準備日後核准之分錄

| | |
|:---|:---|
| Name | 於期末財務報表準備日後核准之分錄 |
| Slug / Wire key | `post_period_approval` / `postPeriodApproval` |
| Rationale | 期末後才核准的分錄可能是操縱期末數字 |
| Preconditions | GL 含 `ApprovalDate`；專案設定 `LastAccountingPeriodDate` |
| Input | GL |
| Predicate | `WHERE ApprovalDate >= LastAccountingPeriodDate` |
| Output | Tag |
| N/A When | 無 `ApprovalDate` 欄位 OR 0 筆符合 |
| WP Sheet | `期末後核准` |

### 分錄摘要出現特定描述

| | |
|:---|:---|
| Name | 分錄摘要出現特定描述 |
| Slug / Wire key | `suspicious_keywords` / `suspiciousKeywords` |
| Rationale | 摘要含調整 / 沖銷 / 錯誤等關鍵字可能是異常 |
| Preconditions | GL 含 `DocumentDescription` |
| Input | GL |
| Predicate | `WHERE REGEX_MATCH(UPPER(TRIM(DocumentDescription)), KEYWORDS)` |
| Keywords | 見 [附錄：摘要關鍵字預設清單](#附錄摘要關鍵字預設清單) |
| Output | Tag |
| 自訂變體 | 進階篩選條件 `customKeywords`（使用者關鍵字，§6.2） |
| WP Sheet | `摘要特定描述` |

### 未預期出現之特定借貸組合

| | |
|:---|:---|
| Name | 未預期出現之特定借貸組合 |
| Slug / Wire key | `unexpected_account_pair` / `unexpectedAccountPair` |
| Rationale | 「Revenue 貸方 + Receivables/Cash/Receipt in advance 借方」為虛假收入最常見模式 |
| Preconditions | AccountMapping 已匯入且包含 Revenue 與至少一個 (Receivables / Cash / Receipt in advance) 類 |
| Input | GL ⨝ AccountMapping |
| Predicate | **Step 1**：子集 CreditSet = `WHERE Category = 'Revenue' AND AmountScaled < 0`<br>**Step 2**：子集 DebitSet = `WHERE Category IN ('Receivables','Cash','Receipt in advance') AND AmountScaled >= 0`<br>**Step 3**：同一 `DocumentNumber` 同時出現在 CreditSet 與 DebitSet |
| Output | Tag (標記在符合的那些分錄行上) |
| 借貸側判定 | `AmountScaled >= 0` 屬借方側、`< 0` 屬貸方側——與 `DrCr` 推導及科目配對分析（§6.1）一致（2026-06-11 統一裁決：0 元分錄歸借方側） |
| 自訂變體 | 進階篩選條件 `accountPair`（使用者指定分類與模式，§6.1） |
| WP Sheet | `未預期借貸組合` |

### 分錄金額中有連續零的尾數

| | |
|:---|:---|
| Name | 分錄金額中有連續零的尾數 |
| Slug / Wire key | `trailing_zeros` / `trailingZeros` |
| Rationale | 整數金額 (如 10,000、1,000,000) 可能為估計或人為捏造。有效高風險條件須與授權金額門檻組合(圓整數**且**大額);以進階篩選 `customTrailingZeros(N)` AND `NumRange(金額≥門檻)` 表達。 |
| Preconditions | 無 |
| Input | GL |
| Predicate | **Step 1**：固定預設 6 個尾數 0(`TrailingZeroThreshold.DefaultZerosThreshold`;可設定性走進階篩選 `customTrailingZeros` 1–12)。<br>**Step 2**：整數取模 `amount_scaled % @zeroModulus = 0`；`@zeroModulus = MoneyScale × 10^N`(N 個 0;6 個 0 ⟺ 可被 10^6 整除)。 |
| Output | Tag |
| 自訂變體 | 進階篩選條件 `customTrailingZeros`（固定位數 1–12 取代動態計算，§6.2） |
| WP Sheet | `連續零尾數` |

### 依分錄編製者彙總

| | |
|:---|:---|
| Name | 依分錄編製者彙總分錄 |
| Slug / Wire key | `creator_summary` / `creatorSummary` |
| Rationale | 分錄集中於少數人員可能代表職能分離不足 |
| Preconditions | GL 含 `CreatedBy` |
| Input | GL |
| Aggregation | `GROUP BY CreatedBy` (若有 `IsManual` 則 `GROUP BY CreatedBy, IsManual`)<br>`SELECT SUM(DebitAmountScaled), SUM(CreditAmountScaled), COUNT(*)` |
| Output | 彙總表 (非 tag) — 供審計人員判讀，不可作進階篩選列述詞 |
| WP Sheet | `編製者彙總` |

### 較少使用之科目

| | |
|:---|:---|
| Name | 較少使用之科目 |
| Slug / Wire key | `rare_accounts` / `rareAccounts` |
| Rationale | 低頻率使用的科目可能被用來隱藏不當分錄 |
| Preconditions | 無 |
| Input | GL |
| Aggregation | `GROUP BY AccountCode, AccountName`；`SELECT COUNT(*), SUM(DebitAmountScaled), SUM(CreditAmountScaled)`；`ORDER BY COUNT(*) ASC` |
| Output | 彙總表 (非 tag)，不可作進階篩選列述詞 |
| WP Sheet | `罕用科目` |

### 週末過帳 / 核准之分錄

| | |
|:---|:---|
| Name | 於週末過帳或核准的分錄 |
| Slug / Wire key | `weekend_posting`（總帳日）與 `weekend_approval`（核准日）/ `weekendPosting`、`weekendApproval`（wire 摘要物件 `weekendActivity`） |
| Rationale | 正常企業運作不應週末處理傳票 |
| Preconditions | GL 含 `ApprovalDate` 或 `PostDate` |
| Input | GL ⨝ DateDimension |
| Predicate | `WHERE DateDimension.IsWeekend = TRUE AND DateDimension.IsMakeupDay = FALSE` (台灣：補班日排除) |
| Output | Tag（過帳／核准各一） |
| WP Sheet | `週末過帳核准` |

### 假日過帳 / 核准之分錄

| | |
|:---|:---|
| Name | 於國定假日過帳或核准的分錄 |
| Slug / Wire key | `holiday_posting`（總帳日）與 `holiday_approval`（核准日）/ `holidayPosting`、`holidayApproval`（wire 摘要物件 `holidayActivity`） |
| Rationale | 假日不應處理傳票；含彈性假日 |
| Preconditions | 使用者已上傳假日曆；GL 含 `ApprovalDate` 或 `PostDate` |
| Input | GL ⨝ DateDimension |
| Predicate | `WHERE DateDimension.IsHoliday = TRUE` |
| Output | Tag（過帳／核准各一）+ 假日名稱（契約擴充待定，見 manifest `import.holiday` 備註） |
| WP Sheet | `假日過帳核准` |

### 回溯過帳之分錄

| | |
|:---|:---|
| Name | 過帳日早於傳票日之分錄（回溯過帳） |
| Slug / Wire key | `backdated_posting` / `backdatedPosting` |
| Rationale | 過帳日早於傳票（憑證）日，可能是回溯記帳以操縱會計期間歸屬，為公認舞弊跡象（屬非財務 RDE，對應事務所底稿高風險條件「過帳日期早於傳票日期」） |
| Preconditions | GL 含選填 `VoucherDate` 欄；傳票日為空者不命中（NULL 安全） |
| Input | GL |
| Predicate | `WHERE voucher_date IS NOT NULL AND post_date < voucher_date`（日期比較沿用既有過帳／核准日的正規化日期作法） |
| Output | Tag — 落地 `result_rule_run`，可作進階篩選列述詞 |
| N/A When | 無 `VoucherDate` 欄位（來源未配對傳票日）OR 0 筆符合 |
| WP Sheet | `回溯過帳` |

### 非授權編製人員

| | |
|:---|:---|
| Name | 由非授權人員編製之分錄 |
| Slug / Wire key | `non_authorized_preparer` / `nonAuthorizedPreparer` |
| Rationale | 分錄由不在查核團隊維護之授權名單內的人員編製，可能是越權或不當記帳（屬非財務 RDE，對應事務所底稿「傳票建立人員不在授權清單」） |
| Preconditions | 已匯入授權編製人員清單（見下文「授權編製人員清單匯入」）；GL 含 `CreatedBy` |
| Input | GL ⨝ 授權編製人員清單 |
| Predicate | `WHERE created_by IS NOT NULL AND TRIM(created_by) <> '' AND TRIM(created_by) NOT IN (SELECT name FROM target_authorized_preparer)`（`created_by` 空白者不命中——無從判定授權與否） |
| Output | Tag — 落地 `result_rule_run`，可作進階篩選列述詞 |
| N/A When | 授權清單尚未匯入（`status="na"`、`naReason` 說明未匯入名單）OR 0 筆符合 |
| 雙重閘控 | 預篩選端以 `HasAuthorizedPreparers`（名單表非空）放行，未匯入則整條 `na`；filter 端 validator 對空名單回 `invalid_scenario`，述詞另以 `EXISTS` 自保，避免「`NOT IN` 空集合 → 全部命中」的反轉風險（鏡射 `unexpectedAccountPair` 需科目配對的閘控模式） |
| escalation | 此 row-tag 可入篩選情境；查核員存「非授權編製人員」情境後，用 D1 `query.filterHitsPage` 取回**全部命中分錄**（C 不另造 escalation 基礎設施） |
| WP Sheet | `非授權編製人員` |

### 低頻編製者

| | |
|:---|:---|
| Name | 由低頻編製者編製之分錄 |
| Slug / Wire key | `low_frequency_preparer` / `lowFrequencyPreparer` |
| Rationale | 全年編製分錄筆數過少之人員（< 12 筆，即 ≤ 11），其編製之分錄風險較高（對應事務所底稿「編製人員張數 ≤ 11」） |
| Preconditions | GL 含 `CreatedBy` |
| Input | GL |
| Predicate | `WHERE created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= 11)`（門檻取 Domain 常數 `PreparerFrequency.DefaultMaxEntries = 11`，參數綁定） |
| Output | Tag — 落地 `result_rule_run`，可作進階篩選列述詞 |
| N/A When | 0 筆符合 |
| 自訂變體 | 進階篩選條件 `customPreparerEntryCount`（門檻 `maxEntries ≥ 1` 由查核員輸入，取代固定的 11；同一述詞、單一事實，§6.2） |
| WP Sheet | `低頻編製者` |

### 低頻科目

| | |
|:---|:---|
| Name | 屬低頻科目之分錄 |
| Slug / Wire key | `low_frequency_account` / `lowFrequencyAccount` |
| Rationale | 全期分錄筆數過少之科目（< 12 筆,即 ≤ 11),其分錄較易藏匿違規或錯誤,風險較高(對應事務所底稿高風險條件 step3 C9「較少使用之科目」,科目張數 ≤ 11) |
| Preconditions | GL 含 `AccountCode` |
| Input | GL |
| Predicate | `WHERE account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= 11)`(門檻取 Domain 常數 `AccountFrequency.DefaultMaxEntries = 11`,參數綁定;純 ANSI,自足無外部清單,無 `NOT IN` 反轉風險) |
| Output | Tag — 落地 `result_rule_run`,可作進階篩選列述詞 |
| N/A When | 0 筆符合 |
| 自訂變體 | 進階篩選條件 `customAccountEntryCount`(門檻 `maxEntries ≥ 1` 由查核員輸入,取代固定的 11;同一述詞、單一事實,§6.2) |
| escalation | 此 row-tag 可入篩選情境;查核員存「低頻科目」情境後,用 D1 `query.filterHitsPage` 取回**全部命中分錄**(本補遺不另造分頁) |
| 與 `rare_accounts`(R6)關係 | 兩者並存不取代:`rare_accounts` 為 top-50 **彙總視圖**(`RuleShape.Aggregate`,供判讀,**不可作進階篩選列述詞**);`low_frequency_account` 為同維度的 **RowTag 版本**(可作列述詞、可入情境)。鏡射 `low_frequency_preparer`(R11)與 `creator_summary`(R5)彙總並存的模式 |
| WP Sheet | `罕用科目` |

### 摘要空白

| | |
|:---|:---|
| Name | 摘要空白之分錄 |
| Slug / Wire key | `blank_description` / `blankDescription` |
| Rationale | 摘要缺漏的分錄無法說明交易性質，屬資料品質與風險雙重指標 |
| Preconditions | 無 |
| Input | GL |
| Predicate | `WHERE DocumentDescription IS NULL OR TRIM(DocumentDescription) = ''` |
| Output | Tag |
| WP Sheet | `摘要空白` |

> 原本的 A2 到 A4「自訂變體」已經不再是獨立的預篩選規則。自訂關鍵字（`customKeywords`）、自訂科目配對（`accountPair`）、自訂尾數位數（`customTrailingZeros`）現在一律以進階篩選條件的形式呈現（見 §6）。它們可以和預設規則同時使用，不再互斥。分工是這樣的：預篩選跑預設規格供初步判斷，進階篩選則用自訂參數進一步收斂母體。

#### 與 legacy 的已知差異（待業務確認是否要補齊）

對照 `legacy/ideascript.bas`，現行的預篩選規則已經涵蓋了 legacy 的對應意圖（罕見人員、罕見科目、週末、假日、人工傳票等）。下列兩處是刻意記錄下來、尚未補齊的細微差異，它們不是缺陷。如果業務確認需要補齊，那屬於新增規則，必須先更新本 §5 與 `docs/action-contract-manifest.md`，再另案實作；這不在資料流程加固的範圍內。

1. **編製者列級標記已補齊（子專案 C）**：`creator_summary` 目前仍是 top-50 的彙總表，只供判讀，不能當進階篩選的列述詞，這一輪不動它。至於「篩出某個低頻人員的全部分錄」與「篩出非授權人員的全部分錄」這兩個需求，現在由兩條 RowTag 規則支援：`low_frequency_preparer`（歷史代號 R11，門檻固定為 ≤ 11）與 `non_authorized_preparer`（歷史代號 R10，名單比對）。這兩條規則可以存成篩選情境，再用子專案 D1（全量明細分頁基礎設施）的 `filterHitsPage` 取回全部分錄。低頻門檻另外還有進階篩選條件 `customPreparerEntryCount` 可供自訂。
2. **尾數比對只限於「連續零」**：`trailing_zeros` 與進階篩選的 `customTrailingZeros` 都只判斷尾端的連續零。legacy 另外還有任意尾數樣式的比對（例如結尾是 9 或 99）。如果要支援這種比對，需要在進階篩選新增一個尾數樣式條件。

### 結果狀態碼

| Status | 意義 |
|:---|:---|
| `V` | 已執行且有結果 |
| `N/A` | 未執行 (缺欄位/設定檔) 或已執行但 0 筆命中 |

### 附錄：摘要關鍵字預設清單

| 關鍵字 | 語言 | 審計意義 |
|:---|:---|:---|
| ADJ | EN | Adjustment |
| REV | EN | Reversal |
| RECLASS | EN | Reclassification |
| SUSPENSE | EN | Suspense |
| ERROR | EN | Error |
| WRONG | EN | Wrong |
| 調整 | ZH | 調整分錄 |
| 迴轉 | ZH | 迴轉分錄 |
| 沖銷 | ZH | 沖銷分錄 |
| 重分類 | ZH | 科目重分類 |
| 避險 | ZH | 避險交易 |
| 重編 | ZH | 重新編製 |
| 錯誤 | ZH | 錯誤更正 |
| 計畫外 | ZH | 計畫外的調整 |
| 預算外 | ZH | 超出預算的調整 |
| 帳外 | ZH | 帳外交易 |

比對方式：`REGEX_MATCH(UPPER(TRIM(DocumentDescription)), '<關鍵字 1>|<關鍵字 2>|…')`

---

## 6. 進階篩選邏輯

進階篩選位於 Step 4。審計人員以預篩選的結果為基礎，疊加各種條件去做組合，最終收斂出真正要拿去測試的分錄母體。各條件型別與其 AST 形狀的 wire 契約，見 `docs/action-contract-manifest.md` 的 Filter / Criteria 章節。

### 6.1 科目配對分析（三種模式）

這是 filter 的條件型別 `accountPair`（slug 為 `account_pair`）。它的前置條件是科目配對表已匯入，而且分類值只能落在 §2.3 列出的白名單內。借貸側的判定方式統一為：`AmountScaled >= 0` 屬借方側，`< 0` 屬貸方側，這跟 `DrCr` 的推導一致。

| pairMode | 邏輯 | 使用時機 |
|:---|:---|:---|
| **exact 精確** | 同一傳票同時有「指定借方分類 + AmountScaled >= 0」與「指定貸方分類 + AmountScaled < 0」的分錄 → 兩類分錄行都輸出 | 已知可疑的借+貸組合 |
| **debitAnchor 借方錨定** | 先找出含「指定借方分類 + AmountScaled >= 0」的傳票 → 輸出借方分錄 + 同傳票所有貸方分錄 (AmountScaled < 0) | 已知可疑的借方，想看對方科目 |
| **creditAnchor 貸方錨定** | 先找出含「指定貸方分類 + AmountScaled < 0」的傳票 → 輸出貸方分錄 + 同傳票所有借方分錄 (AmountScaled >= 0) | 已知可疑的貸方，想看對方科目 |

### 6.2 可疊加的自訂條件

| 條件（filter type） | 邏輯 |
|:---|:---|
| 預篩選標記組合（`prescreen`） | row-tag 規則的述詞即時計算，如 `期末後核准 AND 未預期借貸組合`、`摘要特定描述 OR 連續零尾數` |
| 日期區間（`dateRange`） | `PostDate / ApprovalDate BETWEEN … AND …` |
| 金額區間（`numRange`） | `ABS(AmountScaled) BETWEEN … AND …` |
| 文字比對（`text`） | 指定欄位 contains / exact / notContains / notExact，不分大小寫 |
| 借 / 貸限定（`drCrOnly`） | `DrCr = 'DEBIT'` 或 `'CREDIT'` |
| 人工 / 自動（`manualAuto`） | `IsManual = TRUE/FALSE`（NULL 永不匹配） |
| 期內 / 期外（`periodInOut`） | `PostDate` 在 / 不在會計期間（含邊界；NULL 兩側皆不命中） |
| 自訂關鍵字（`customKeywords`，原 A2） | 同摘要特定描述述詞，關鍵字為使用者輸入 |
| 自訂尾數位數（`customTrailingZeros`，原 A4） | 同連續零尾數述詞，位數可設定 1–12（取代 prescreen 的固定預設 6）；與 `NumRange` 組合即授權金額門檻閘 |
| 自訂編製人員張數（`customPreparerEntryCount`） | 同低頻編製者述詞（`COUNT(*) <= @maxEntries`，依 `created_by` 分組），門檻 `maxEntries ≥ 1` 由使用者輸入，取代 prescreen 的固定預設 11 |
| 自訂科目張數（`customAccountEntryCount`） | 同低頻科目述詞(`COUNT(*) <= @maxEntries`,依 `account_code` 分組),門檻 `maxEntries ≥ 1` 由使用者輸入,取代 prescreen 的固定預設 11 |
| 科目配對分析（`accountPair`，原 A3） | 見 §6.1 三模式 |
| 季末前借記收入（`revenueDebitNearQuarterEnd`，KCT 清單 A） | 科目=Revenue 且借方側(`AmountScaled >= 0`)，且 `PostDate` 落在曆年季底(3/31、6/30、9/30、12/31)前 `windowDays` 天(含季底當日)的視窗；視窗由查核期間＋天數枚舉，`windowDays` 整數 1–92；需科目配對 |
| 收入無一般對方科目（`revenueWithoutNormalCounterpart`，KCT 清單 C） | 本列為 Revenue 貸方(`AmountScaled < 0`)，但同傳票無「借方側且分類 ∈ {Receivables, Receipt in advance}」的分錄(不含 Cash)——`unexpectedAccountPair` 的否定面；需科目配對 |
| 收入之人工分錄（`manualRevenueEntry`，KCT 清單 D） | 科目=Revenue 且 `IsManual = TRUE`(NULL 永不匹配)；需科目配對 |
| 特定金額尾數（`trailingDigits`，KCT 清單 H） | 顯示金額主單位整數(`ABS(AmountScaled) / MoneyScale`，捨小數)末 k 位等於任一指定尾數樣態(`keywords` 承載，每組 1–12 位純數字；`AmountScaled <> 0`) |
| 編製與核准同一人（`preparerEqualsApprover`，KCT 清單 J） | `created_by` 與 `approved_by` 皆非空白且(忽略大小寫與前後空白)相等 |

### 6.3 KCT 小組條件分組（Phase 1）

進階篩選的條件型別在前端是依審計意圖分組呈現的。自 2026-06-23 起，新增了一個獨立的「KCT 小組條件」分組，用來承載 KCT 小組的方法學清單（KCT 是查核團隊內負責這套方法學的小組代號 [待確認：原文未展開 KCT 全稱]）。這套清單共十條。其中 A、C、D、H、J 這五條，是上表新增的專屬 filter type。E（特定人員）、F（特定摘要）、G（空白摘要）、I（非營業日）這四條則不另立新型別，而是由前端 KCT 分組的「預設按鈕」帶入既有型別的預填規則，分別對應到 `text`、`customKeywords`、`prescreen blankDescription`、`prescreen weekendPosting OR holidayPosting`。至於 B（借記固定資產、貸記費用）這一條，因為需要新增資產負債表（BS）與損益表（IS）的科目分類維度，所以列為 Phase 2，要等 KCT 交付完整的分類清單後再另案實作。設計快照見 `docs/specs/2026-06-23-kct-advanced-filter-conditions-design.md`。

---

## 7. 審計工作底稿

Step 5 會把完整的測試結果匯出成 Excel 檔案，作為審計證據的書面留存。這個檔案包含下列工作表：

| Sheet | 內容 |
|:---|:---|
| `Engagement Overview` | 客戶、期間、財報準備起始日、編製人與編製日 |
| `Data Overview` | GL 筆數/借方/貸方/淨額、TB 科目數 |
| `Validation Overview` | 四項資料驗證異常計數 |
| `完整性測試明細` | 完整性測試差異明細 |
| `借貸不平明細` | 借貸不平測試的不平衡傳票 |
| `INF 抽樣明細` | INF 抽樣測試的抽中樣本 |
| 各預篩選規則分頁 | 分頁名 = 命名登錄表的中文 WP Sheet 名（`期末後核准`、`摘要特定描述`…，見 §5；Status = V 才有資料） |
| 各篩選情境分頁 | 已保存篩選情境的命中明細（情境名 + 查核理由） |
| `Account Mapping Info` | 使用的科目配對表 |
| `Field Mapping Info` | 原始欄位 → 標準欄位對應、規則 slug ↔ 中文分頁名對照（命名追溯） |

### 7.1 欄位名稱與可追溯性

審計員最終的底稿，在需要追溯來源資料時，應該顯示原始資料的欄位名稱。標準化欄位主要是給內部使用的，用於內部查詢、完整性測試、預篩選規則、進階篩選，以及與 provider 無關的執行。因此 `Field Mapping Info` 這張表必須清楚地把原始欄位和標準欄位連結起來，避免底稿上只剩內部欄位名稱、導致難以回查客戶資料。

### 7.2 匯出底稿的 writer（子專案 E1：工作底稿匯出 writer，已實作，待 GUI 驗收）

上面 §7 列的 Sheet 清單，是底稿在邏輯內容上的目標。實際產出的 `.xlsx`（固定命名為 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`）由 Infrastructure 的 `WorkpaperWriter` 串流寫出，版面對齊事務所「JE Testing Tool」樣本，內容包含封面、step1 到 step5 的測試底稿，以及三張參考資料表。`export.*` 不再是委派給前端的 stub：現在 `export.workpaperStream` 由 Application handler 編排，由 writer 串流寫檔。程式碼已經落地、自動化測試全部通過，但 GUI 的目視驗收還沒進行。

> **狀態誠實聲明：** 本節描述的是已經實作、並通過自動化測試的後端能力。但有些屬於 GUI 互動的部分還沒驗收，包括逐張 Excel 目視對齊樣本、大母體匯出時不爆記憶體等，這些仍須在 Windows 端做人工驗收（見 `docs/windows-handoff.md` 的任務卡）。另外，欄位配對匯入的 round-trip（也就是把 Field Mapping Info 讀回來重建配對）延後到子專案 E2 處理，不在這一棒的範圍內。

#### writer 是 deep module，每張 sheet 是資料而不是分支

`WorkpaperWriter` 對外只暴露一個窄介面 `WriteAsync(Stream, context) → ExportStats`，把全部 OpenXML SAX 的細節都藏在內部，包括 shared strings inline、styles、merged cells、number formats 與串流。資料表的產出沿用單一一個 `EmitTableSheet` 原語，它由表頭區塊、欄標，加上一個「列來源」委派組成；委派內部以 keyset 逐頁向 repo 取資料，再 yield 出 cell 陣列。封面、固定文字、手填骨架則各自是小的 emitter。要強調的是，條件表（例如 step1-3-1 只在差異不等於 0 時才出現）是 orchestration 層的 guard，而不是某個 god-function 裡的特例分支。也就是說，要 emit 哪些 sheet 是由 orchestrator 依資料動態決定的，emitter 本身完全沒有「我是第幾張表」這種分支。

#### 串流寫出，不整份載入（與 §1.5.5 一致）

writer 走的是 `DocumentFormat.OpenXml` 的 SAX（`OpenXmlWriter`），大型明細列用 inline string，逐頁逐列寫出。它不會把整份 result set 載入 `DataTable`、`List<>` 或 DOM workbook。百萬列的傳票與明細是逐頁流過寫出器的，所以記憶體用量有界。要注意 ClosedXML 只能用於 dev fixture（`DemoWorkbookWriter`），絕不可拿來當底稿的寫出器，這是鐵律（見 §1.5.5）。

#### writer 是查詢結果的消費者，不重做業務邏輯

writer 注入既有的 ProviderRouting 查詢 repo 與 store，對 repo 以 keyset 逐頁取資料、逐列寫出。其中 scaled 值換算成顯示值（`(decimal)scaled / moneyScale`）這一步是在 writer 內做的，但它只是純算術，writer 內不會重算任何規則。各張表的 JET 資料來源如下：

| 工作表 | 來源 |
|:---|:---|
| step1 完整性測試（全科目） | `query.completenessDiffPage`（逐頁，全科目） |
| step1-1 借貸不平測試 | 結論文字 + `query.docBalancePage`（**有不平傳票才 emit 例外表**） |
| step1-2 分錄編製人員說明 | **全編製人員彙總查詢（E1 新增，去 LIMIT 不截斷）** |
| step1-3 完整性差異說明 / step1-3-1 完整性差異調節 | `query.completenessDiffPage` WHERE diff≠0；**step1-3-1 為條件表，差異≠0 才 emit** |
| step2 可靠性測試 | `query.infSamplePage`（抽樣行）；**金額寫借/貸兩欄**（E=借方、F=貸方），對齊 D1 已回的 `DebitScaled`/`CreditScaled`，不寫借貸代號+單欄 |
| step3 高風險條件彙總 | `query.tagMatrixScenarios` |
| step4 符合高風險條件傳票 | `query.tagMatrixVoucherPage`（逐頁；C1..CN 以 matchedPositions 標 Y） |
| step4-1 符合高風險條件傳票明細 | `query.tagMatrixRowPage`（逐頁）+ **動態 `C{position}_TAG` 欄**（僅 emit 有行層命中的 position 之欄；以 matchedPositions 標 Y） |
| 自動化工具-檔案欄位資訊 | 欄位配對設定 + `GlCanonicalNames`（正準中文名） |
| 自動化工具-假期假日資訊 | 行事曆 store（週末表 + 假日/補班表） |
| 自動化工具-科目配對資訊 | 科目配對 store；**GL 有、TB 無的科目 `GL_NAME` 寫字面值 `Not in TB`**（B 已落地的 Not-in-TB 記號） |

#### 正準中文名的單一事實來源（在 Domain 內）

Field Mapping Info，以及子專案 E2 的 round-trip，都需要一份「邏輯鍵（logical key）對應正準中文名」的對照，例如 `docNum` 對應 `傳票號碼_JE`、`postDate` 對應 `總帳日期_JE`，TB 側則有 `會計科目編號_TB` 等。這份對照由 `Domain/GlCanonicalNames`（邏輯鍵對應正準中文名）承載，是單一一個資料結構，匯出與日後的 round-trip 共用同一份。它和既有的 `GlFieldWhitelist`（邏輯鍵對應實體欄）是分開的兩份，但概念上相鄰。

#### 多情境矩陣的惰性 materialize 交給 handler 負責

step3、step4、step4-1 的高風險矩陣來自子專案 D2 的 `tagMatrix*` 查詢。這套查詢要求「全部已存情境都必須先把命中落地」，這個惰性補算的動作由 handler 負責（共用 `FilterRunMaterializeService`）。writer 只負責讀取已經備妥的查詢結果，本身不會去觸發 materialize。

#### 手填欄一律留空

方法學裡標示為手填的欄位，由 writer 寫出空白骨架供查核員填寫，E1 不會自動去計算這些值。這些手填欄包括 step1-2 的部門、職稱、說明；step1-3 的原因、調節、調節後；step1-3-1 的前期損益金額；step2 的結果 A 到 G 與詳細說明；step4 的 P 到 U；以及 step5 的內文。封面的 CAATs 段只寫一個檔名字串 `{客戶}_CAATS_JE_WP_{yyyymmdd}.docx`，並不會真的產生這份 docx，因為那是外部文件、由查核員自備。

完整的逐表內容規格、來源對碼與設計理由見 `docs/specs/2026-06-21-workpaper-export-writer-design.md`。wire 契約（`export.workpaperStream`、`host.selectSavePath`）見 `docs/action-contract-manifest.md`。

---

## 8. 台灣在地化

JET 針對台灣的審計環境，需要處理彈性假日、補班日與中文關鍵字這類在地化規則。

### 8.1 彈性假日與補班日

- **彈性假日**：當國定假日落在週二或週四時，會把前一個週一或後一個週五也放假。
- **補班日**：因為彈性假日而改成要上班的那個週六。

實作上要注意兩件事：
- 週末規則必須排除 `IsMakeupDay = TRUE` 的日期，因為補班的週六實際上是工作日。
- 假日規則必須納入 `IsHoliday = TRUE` 的工作日，因為彈性放假的那天本來是平日。

### 8.2 中文摘要關鍵字

「分錄摘要出現特定描述」規則的預設關鍵字清單，已經包含了台灣審計常見的中文字彙，清單見上文附錄。

### 8.3 專案元資料

| 欄位 | 值 |
|:---|:---|
| `Version` | `TW` |
| `Language` | `CHT` |
| `PeriodStartDate` | 會計期間起始 |
| `PeriodEndDate` | 會計期間結束 |
| `LastAccountingPeriodDate` | 期末後核准規則的基準日 |

---

# B. 技術決策

## 9. 技術約束與排除選項

JET 的正式方案受到三個硬限制約束：

1. 前端必須能用 HTML / CSS / JS 快速迭代審計工作流程。
2. V/R/Filter 計算必須交給資料庫以 set-based SQL 執行。
3. 正式方案必須符合 Windows、.NET、以及企業資安與部署上的限制。此外，公司禁止把 Python 當成正式產品的技術路徑。

基於這三個限制，下列選項都不會被當成正式架構：

| 選項 | 不採用原因 |
|:---|:---|
| Caseware IDEA / IDEAScript | 不再訂閱 IDEA 授權；`.IDM` 與 `client.OpenDatabase` 是專有執行環境 |
| Excel VBA + Access | Access 單檔 2GB 上限、巨集安全政策、測試與 AI 協作能力不足 |
| Python pipeline | 公司資安規範不允許作為正式產品路徑 |
| Electron | HTML UI 強，但額外帶 Chromium / Node.js，部署與資安審查成本高 |
| Tauri | Windows UI 可用 WebView2，但後端主力是 Rust，會破壞 .NET / SQL Server / Visual Studio 主線 |
| Blazor Hybrid | 可行但會引入 Razor/component runtime，與「靜態 HTML 模板 + JetApi contract」方向不一致 |
| Local ASP.NET Core server | 架構乾淨，但會變成本機 server / port / firewall 問題，違反不架 server、不開 port 的部署限制 |

`legacy/` 裡的內容只能拿來對照規則語意、作歷史參考，不能當成實作的來源。正式開發一律依本文件、`docs/action-contract-manifest.md` 與 `docs/jet-frontend-description.md` 進行。

## 10. 為什麼選 .NET 10 + WinForms + WebView2 + HTML

下表逐項說明技術選型，並列出每一項被排除的替代選項與原因：
| 項目 | 選擇 | 被排除的選項與理由 |
|:---|:---|:---|
| 語言 | **C#** | VB.NET — 社群小、AI 品質低 |
| 執行時 | **.NET 10 LTS** | .NET Framework — 不支援現代 CLI / AI workflow |
| 桌面 Host | **WinForms** | WPF / WinUI 3 對薄 host 沒有足夠收益；Blazor Hybrid 會改變前端形態 |
| UI 引擎 | **WebView2** | 原生 WinForms 控件 — AI 不擅長生成，UI 迭代慢 |
| 前端語言 | **HTML / CSS / JS** | AI 生產力最高；`docs/jet-template.html` 已有設計模板 |
| 本機資料庫 | **SQLite** | 散落 JSON — 不利狀態管理與查詢統一；DuckDB 列為分析型 provider 候選但不作為起始依賴 |
| 主資料庫 | **SQL Server** | Access — 資料量天花板；PostgreSQL / MySQL — 企業 Windows 環境已標配 SQL Server |
| IDE | **Visual Studio 2026** | VS Code — 對 WinForms Designer 支援不足 |
| AI 主力 | **GitHub Copilot Agent Mode** | — |
| AI 輔助 | Codex CLI / Claude Code | 跨檔重構與大規模改動 |

**為什麼採用「WinForms 包住 WebView2、再載入 HTML」這層夾心結構**，理由有三個：
- WinForms 能打包成單一一個 .exe，這符合資安與部署上的限制，也就是不架 server、不開 port。
- WebView2 用來承載 HTML 前端，而 HTML 前端正是 AI 最擅長產生的。
- `Form1` 維持極薄，只當 WebView2 的容器，不放任何業務邏輯。

核心決策可以濃縮成一句話：WinForms 只當薄薄的桌面外殼，HTML 只當 UI，Bridge 只當傳遞 JSON action 的通道，Application 只負責 orchestration（編排），而所有規則計算都在 Infrastructure provider 的資料庫 SQL 裡完成。

---

# C. 系統架構

## 11. 架構總覽

```
┌─ WinForms Host (.exe) ──────────────────────────────┐
│ ┌─ WebView2 Runtime ─────────────────────────────┐ │
│ │ ┌─ HTML / CSS / JS Frontend ─────────────────┐ │ │
│ │ │ (source: docs/jet-template.html,           │ │ │
│ │ │  packaged under src/JET/JET/wwwroot/)      │ │ │
│ │ └─────────────────┬──────────────────────────┘ │ │
│ └──────────────────┬┼─────────────────────────────┘ │
│                    ↕ action + payload (JSON)        │
│ ┌─ Thin Bridge + Action Dispatcher ────────────────┐│
│ │   postMessage handler + action → handler map     ││
│ └──────────────────┬───────────────────────────────┘│
│                    ↕                                │
│ ┌─ Application (CQRS) ────────────────────────────┐ │
│ │   Commands / Queries / Handlers                 │ │
│ │   (depend only on Domain interfaces)            │ │
│ └──────────────────┬──────────────────────────────┘ │
│                    ↕                                │
│ ┌─ Domain (pure) ──────────────────────────────────┐│
│ │   Entities / Value Objects / Rule Specs          ││
│ │   IGlRepository / ITbRepository / ...            ││
│ └──────────────────┬───────────────────────────────┘│
│                    ↕                                │
│ ┌─ Infrastructure ────────────────────────────────┐ │
│ │   SqliteProvider     │   SqlServerProvider      │ │
│ │   FileReader (Excel/CSV) / OpenXML Exporter     │ │
│ └──────────────────┬──────────────────────────────┘ │
└────────────────────┼────────────────────────────────┘
                     ↕
            ┌────────┴────────┐
         SQLite (本機)    SQL Server (企業)
```

### 架構模式總結

| 模式 | 套用範圍 |
|:---|:---|
| **Thin-Bridge** | WebView2 ↔ .NET 之間只有 `postMessage` + JSON，不夾邏輯 |
| **Action Dispatcher** | 單一進入點 (字典查表) 把 `action` 分派到 Handler |
| **Application CQRS** | Commands (變更) 與 Queries (讀取) 分離，各自有 Handler |
| **Clean Core** | `Domain` 不依賴任何框架、I/O 或 UI；`Application` 只依賴 `Domain`；`Infrastructure` 實作 `Domain` 介面 |
| **Repository 雙 Provider** | 單一 `IGlRepository` 介面，`SqliteGlRepository` 與 `SqlServerGlRepository` 兩個實作同時存在；執行期依設定切換 |

> **本專案不採用完整的 Hexagonal 或 Onion 那一整套五層架構。** 上面這五個角色（Host、Bridge、Application、Domain、Infrastructure）已經夠用了。再往上疊更多層，是在為論文服務，不是在為 JET 服務。

---

## 12. 層級職責

本節逐層說明 Host、Bridge、Action Dispatcher、Application、Domain 與 Infrastructure 各自的職責邊界，以及每一層明確不該做的事。

### Host（WinForms）

- 管理 WebView2 的生命週期。
- 處理系統視窗與檔案對話框。
- 透過同一條 WebMessage action 通道，對外提供少數幾項 host 能力，例如檔案選擇。
- `Form1.cs` 維持極薄，永遠不放業務邏輯。

### Bridge（WebView2 與 .NET 之間）

Bridge 的主通道固定使用 WebView2 的 `postMessage`、`WebMessageReceived` 與 `PostWebMessageAsJson`。前端以一個 JSON envelope 送出請求：
```json
{ "requestId": "<uuid>", "action": "import.gl.fromFile", "payload": { "filePath": "..." } }
```

Bridge 只做三件事：
1. 把收到的訊息反序列化。
2. 呼叫 `ActionDispatcher.Dispatch(action, payload)`。
3. 把結果包成 `{ "requestId", "ok", "data"/"error" }` 回傳。

Bridge 內不得內嵌任何 SQL、規則或檔案操作。

`AddHostObjectToScript` 與 host object 不作為一般的 bridge 模式使用。如果未來真的需要用到，只能限縮在極小、明確、而且不會被 UI 高頻呼叫的 host capability 上，並且不得繞過 action manifest。

### Action Dispatcher

Action Dispatcher 本質上是一個 `Dictionary<string, IActionHandler>`，依 `action` 把請求分派到對應的 Command 或 Query Handler。建議的 action 命名空間如下：

| Namespace | 範例 |
|:---|:---|
| `project.*` | `project.create`、`project.load` |
| `import.*` | `import.gl`、`import.tb`、`import.accountMapping`、`import.holiday` |
| `validate.*` | `validate.run` |
| `prescreen.*` | `prescreen.run`、`prescreen.status` |
| `filter.*` | `filter.preview`、`filter.commit` |
| `export.*` | `export.workpaper` |
| `query.*` | `query.glPage`、`query.validationSummary` |

不要引入 MediatR 來當 in-process 的 mediator。這裡的 CQRS 只是一條程式碼組織原則，不是要引入特定套件。Action Dispatcher 手寫就好，這樣比較便於稽核、測試與控制授權風險。

### Application (CQRS)

```csharp
public sealed record ImportGlCommand(ProjectId ProjectId, string FilePath, GlAmountMode Mode);

public sealed class ImportGlCommandHandler(IGlRepository gl, IGlFileReader reader, IProjectRepository projects)
{
    public async Task<ImportResult> HandleAsync(ImportGlCommand cmd, CancellationToken ct)
    {
        // 1. 驗證 project 存在
        // 2. reader.ReadAsync(cmd.FilePath, cmd.Mode) → IAsyncEnumerable<GlEntry>
        // 3. gl.BulkInsertAsync(...)
        // 4. 回傳 ImportResult
    }
}
```

每一類規則的執行各對應一個 Handler（例如 `validate.run`、`prescreen.run`、`filter.preview`）。所有規則的述詞集中放在 Infrastructure 的單一事實來源 `GlRulePredicates` 裡。因此新增一條規則的工作量是固定的三步：在登錄表加一列、在述詞集裡加一段、在 handler 補上摘要欄位，不需要另外開新的 action。

### Domain（純邏輯層）

- 各種 entity，例如 `GlEntry`、`TbEntry`、`AccountMapping`、`DateDimension`。
- 各種 enum，例如 `GlAmountMode`、`TbChangeMode`。
- `RuleSpec`，也就是聲明式的規則描述，它和第 5 節的規格表是同構的。
- 各 Repository 介面，例如 `IGlRepository`、`ITbRepository`、`IDateDimensionRepository`、`IProjectRepository` 等。
- Domain 層不得有任何框架依賴，也就是不能出現 `using System.Data.*`、`Microsoft.Data.*`、`System.IO.*` 之類的東西。

### Infrastructure

- `SqliteGlRepository` 與 `SqlServerGlRepository`。
- `ExcelGlFileReader` 與 `CsvGlFileReader`（正式匯入格式支援 `.xlsx` 與 `.csv`）。
- `StreamingWorkPaperExporter`（OpenXML SAX writer）。
- `IConnectionFactory`（SQLite 與 SQL Server 各有一個實作）。

---

## 13. SQLite / SQL Server Provider 策略

JET 同時支援兩個資料庫 provider，依案件規模與部署情境來選用，但兩者共用同一組 repository contract。本節說明這兩個 provider 各自的定位、設計如何落實、方言差異怎麼處理，以及如何做等價測試。

SQLite 與 SQL Server 這兩個 provider 都已經全面落地（2026-06-14）。每一個 per-project（每專案）的 repository 都備有 `Sqlite*` 與 `SqlServer*` 兩套實作，再經 `ProviderRouting*` 包裝，依案件所選的 provider 做路由。涵蓋的 repository 包括 Gl、Tb、Import、MappingState、Calendar、AccountMapping、ValidationRun、PrescreenRun、FilterRun、RuleRun、FilterScenario、DataPreview、MessageLog 與 DevDatabaseInspector。完整的工作流程（建案、匯入、配對、驗證、預篩選、進階篩選，含 resume 與刪除）在兩個 provider 下是端到端等價的，這由一組受 LocalDB 閘控的 golden journey 測試實際跑過驗證。DuckDB 目前仍保留為未來候選的分析型 provider，它必須先通過本節的 benchmark gate 才能採用；也因此，初期設計就持續避免把 SQLite 寫死進 Application 或 frontend。

### 核心原則

SQLite 是本機持久案件的 provider，SQL Server 則是大資料量或共享資料情境的 provider。這兩者不是雙寫架構，而是同一組 repository contract 的兩種不同執行實作。

單次案件執行只會使用一個 provider。要用哪一個，可以由案件設定指定，也可以依資料量門檻來建議切換。

兩個 provider 的案件定位如下：

- **SQLite**：適用於小於 1,000 萬筆 GL 列的本機持久案件，提供本機持久資料庫的路徑。
- **SQL Server**：適用於大於 1,000 萬筆 GL 列、多人共享資料、或 10 億列等級的大資料案件的執行路徑。
- **DuckDB**：列為本機分析型 provider 的候選，只有在 benchmark 通過、且資安治理可接受之後才能新增，而且不會取代一開始就採用的 SQLite 路徑。

### 設計落實

```csharp
public interface IGlRepository
{
    Task<int> BulkInsertAsync(ProjectId id, IAsyncEnumerable<GlEntry> rows, CancellationToken ct);
    Task<IReadOnlyList<GlEntry>> QueryAsync(ProjectId id, GlQuerySpec spec, CancellationToken ct);
    Task<RuleResult> RunRuleAsync(ProjectId id, RuleSpec rule, CancellationToken ct);
    // ...
}
```

**兩個實作同時存在**：
- `SqliteGlRepository` 採用 `Microsoft.Data.Sqlite`。
- `SqlServerGlRepository` 採用 `Microsoft.Data.SqlClient`。

Repository contract 只回傳 summary、resultRef 與分頁過的列，不回傳完整母體。Application 層不得感知 provider 的方言或資料庫型別。

### 執行期選擇（實作現況）

Provider 在案件建立時就選定，並寫入 `project.json` 的 `databaseProvider` 欄位（值為 `"sqlite"`（預設）或 `"sqlServer"`；`project.create` 接受這個欄位，而且建立之後就不能再改）。provider 的選擇不走全域 DI 切換，而是每個 repository 各包一層 `ProviderRouting*`：

- `ProjectProviderResolver` 以 projectId 為鍵，快取該案件的 provider。因為 provider 一旦選定就不可變，所以這個快取是安全的。所有路由包裝共用這份快取，避免每次呼叫都重讀 `project.json`。
- 每個 `ProviderRouting*` 方法會先呼叫 `ResolveAsync(projectId)`，再用 `ProviderSelection.Pick` 委派給對應的 `Sqlite*` 或 `SqlServer*` 實作。遇到未知的 provider 就回 `unsupported_provider`。
- Application 層的 handler 只注入 repository 介面，完全不感知 provider 或方言。

隔離模型是「每專案一個資料庫」。SQLite 的資料庫落地在 `projects/{projectId}/jet.db`，這個檔案本身就構成 scope。SQL Server 則是在一個共用 instance 上的 `JET_{projectId}` 資料庫，它的資料表不帶 `project_id` 欄，忠實對應「每專案一個 jet.db」這個模型。SQL Server 的 base 連線字串取自環境變數 `JET_SQLSERVER_CONNECTION`，而 `InitialCatalog` 由 provider 依專案覆寫。如果這個環境變數沒設定，SQLite 專案不受影響；但選了 sqlServer 的案件，會在連線時得到一個明確的 `sql_server_not_configured` 錯誤。要注意 SQL Server Express（用於開發，例如 LocalDB 的 `(localdb)\MSSQLLocalDB`）與 Standard、Enterprise（用於生產）共用同一套實作，兩者的差異僅在連線字串，不會為 Express 另外建一個獨立 provider。

已建立的案件，不得在同一次規則執行中混用 SQLite 與 SQL Server。刪除專案（`project.delete`）時會把資料庫一併移除：SQLite 走刪除 `jet.db` 的路徑（刪之前先呼叫 `SqliteConnection.ClearAllPools()` 釋放檔案鎖），SQL Server 走 `DROP DATABASE JET_{projectId}` 的路徑（drop 之前先 `SET SINGLE_USER WITH ROLLBACK IMMEDIATE`）。這兩條路徑同樣都經過 `ProviderRouting` 路由。

### 金額儲存與計算

SQLite / SQL Server 兩個 provider 的權威金額欄位都使用 scaled integer：

```sql
amount_scaled BIGINT NOT NULL
```

顯示用的 decimal 只存在於 DTO、UI 與匯出格式這幾個邊界上。Provider 的 SQL 不得用 SQLite 的 `REAL` 或文字 decimal 聚合來做審計判斷。

### SQL Server 大資料量的索引策略

在 SQL Server 路徑上，`target_gl_entry` 與大型的 `result_*` fact table 都應該評估使用 columnstore：

- 這些表在匯入之後，主要是以附加和讀取為主的分析型負載，所以適合 clustered columnstore index。
- 匯入時若依 `PostDate` 或專案載入順序排序，可以提高 segment elimination 的機率。
- 至於 `config`、mapping、小型 lookup table，以及 keyset 分頁所需的查詢鍵，仍然可以使用 rowstore index。
- columnstore 是大資料量路徑的預設設計方向。如果某張表決定不用 columnstore，必須在 provider 設計裡說明原因。

### SQL Server 匯入暫存寫入（SqlBulkCopy 串流）

GL/TB 投影（staging 轉 target）與匯入暫存寫入（由 `SqlServerImportRepository` 寫入 `staging_*_raw_row`）都採用 `SqlBulkCopy` 搭配串流的 `DbDataReader`，這是本節指定的大資料寫入形狀。

匯入路徑原本是逐列（row-by-row）的 prepared insert，每一列都要做一次 `ExecuteNonQueryAsync` round-trip。實測 1,403,327 列約需 212 秒，是主要的瓶頸。為此在 2026-06-14 改為 `SqlBulkCopy`（設定 `EnableStreaming = true`、`BulkCopyTimeout = 0`，並沿用既有的 transaction），由一個串流 reader 餵入。這個 reader（`StagingBulkCopyDataReader`）包住一個 `IAsyncEnumerable<StagingRow>`，對外曝出 staging 的 5 個欄位：`batch_id`、`row_number`、`source_no`、`source_row_number`、`row_json`。

`ReplaceBatchAsync` 與 `AppendToBatchAsync` 共用同一個 reader 形狀，它與資料種類無關（kind-agnostic），所以 GL 和 TB 都受惠。AccountMapping 與 Calendar 走的是各自獨立的 store，屬於小量的參考資料，不在這個範圍內。SQLite 路徑（transaction 加 prepared batch，約每秒 44k 列）不需要改動，維持原狀。

**async 與 sync 的橋接**：資料來源是 `IAsyncEnumerable<StagingRow>`，但 `SqlBulkCopy` 需要的是同步的 `DbDataReader`。中間用一個有界的 `Channel` 做 producer-consumer：背景 task 把資料餵進 channel，而 reader 覆寫 `ReadAsync`，改走 `WaitToReadAsync` 與 `TryRead` 來消費。之所以覆寫的是 `ReadAsync`，是因為 `SqlBulkCopy.WriteToServerAsync` 的 async 路徑本來就是靠 `ReadAsync` 來推進每一列；這樣就不會在同步的 `Read()` 裡阻塞 async。

producer 必須用 `Task.Run` 放到獨立的執行緒上跑。原因是 xlsx 的 SAX 解析是 CPU 密集的同步工作，如果把它留在呼叫緒上，會和 bulk copy 互相爭用 CPU，退化成序列執行。實測也證實：不用 `Task.Run` 時，解析與寫入沒有重疊；用了之後才有重疊。

取消或失敗的處理是這樣：linked CTS 會解除那個卡在 `WriteAsync` 的 producer，接著 `await producerTask` 把它收束掉，確保 producer 不會洩漏；至於例外，則交給呼叫端的 `await using transaction` 去 rollback。另外，重匯入時的清除、以及附加時的失效，那種百萬列等級的 `DELETE`，會設 `CommandTimeout = 0`，因為它是長批次，這跟 `BulkCopyTimeout = 0` 是對齊的。

新版的語意和原本的逐列版逐項一致（這由特徵化測試鎖定）：空來源會 rollback、附加時從最大的 `row_number` 繼續往下編、欄位集合在串流結束後於同交易內回寫、取消時整批 rollback 且 `JET_` 庫裡不留任何已提交的殘留。

**實測數字（LocalDB、1,403,327 列）以及比值的本質**：匯入從 211.9 秒（逐列版）降到約 38 秒（SqlBulkCopy 串流版）。耗時可以分解成兩部分：Excel SAX 解析加 JSON 序列化的下限約 29.7 秒，bulk copy 本身約 15.7 秒，兩者重疊之後總計約 38 秒。換句話說，staging 寫入已經不再是瓶頸，主導成本上移到了 xlsx 解析，而那屬於上游，不在這一輪的範圍內。至於 GL 投影約需 17 到 20 秒。

匯入耗時除以投影耗時的比值約為 2.0 到 2.2，並且會因為投影耗時浮動而在 1.9 到 2.2 之間擺動。1.5 是這個比值的結構下限（約等於解析下限除以投影），它不是一個可以再壓低的目標，因為匯入必須解析 114MB 的 xlsx，而投影卻只讀已經提交好的 staging。正因如此，規模測試（`SqlServerImportScaleSmokeTests`）只斷言列數正確，耗時與比值只記錄下來供人工判讀，這也對齊了既有 scale 測試「不做 wall-clock 斷言」的慣例。

### SQL 方言差異的處理

- **盡量使用 ANSI SQL**，例如 `INNER JOIN`、`GROUP BY`、`SUM`。
- **方言差異一律由 Provider 自行封裝**，不讓 Application 層感知。常見的差異點如下：
  - 參數占位符：SQLite 用 `@p`，SQL Server 也可以用 `@p`（兩者都支援，方便統一寫法）。
  - 大量插入（`BULK INSERT`）：SQL Server 用 `SqlBulkCopy`，SQLite 則用 transaction 加上 prepared statement batch。
  - 正則比對（`REGEX_MATCH`）：SQLite 需要註冊 `regexp()` UDF，SQL Server 則用 `PATINDEX` 加 `LIKE`，或寫 CLR function。
  - `FULL OUTER JOIN`：SQLite 不支援，需要用 `LEFT JOIN` 加 `UNION ALL` 這類等價形狀封裝出來；SQL Server 可以直接使用。
  - 連續零尾數的判斷：一律優先用 scaled integer 加取模運算，避免踩到各 provider 字串函式的差異。
- **遇到差異時，優先寫兩版 SQL**，分別放進 `SqliteGlRepository` 與 `SqlServerGlRepository`，不要在 Application 層寫動態的方言切換。

### 全量明細 keyset 分頁（query.*Page）

匯入的母體可達 140 萬列以上，所以明細不可能一次全部回給前端。如果只是互動式的審閱，用 `validate.run` 的 50 列以內預覽、或 `filter.preview` 的 1000 列以內預覽就夠了。但要走訪全量明細時（例如 GUI 的「載入更多」、未來的底稿匯出），就改走一組 keyset 游標分頁的 action：

| Page action | 資料 | 排序／游標鍵（唯一） | 其他參數 |
|:---|:---|:---|:---|
| `query.completenessDiffPage` | 完整性逐科目差異（diff≠0） | `account_code` ASC | — |
| `query.docBalancePage` | 借貸不平傳票 | `document_number` ASC | — |
| `query.nullRecordsPage` | 空值紀錄 | `entry_id` ASC | `category`（空值分類） |
| `query.filterHitsPage` | 單一已存情境命中（行層） | `entry_id` ASC | `scenarioPosition` |
| `query.infSamplePage` | INF 抽樣 60 行明細 | `entry_id` ASC | — |

契約的形狀如下（權威的逐字定義在 `docs/action-contract-manifest.md`）：

- **請求**是 `{ projectId, cursor?, pageSize?, ... }`。其中 `cursor` 在取第一頁時傳 null。`pageSize` 預設為 200、上限為 500，由 Domain 的 `PageCursor` 與頁大小常數做夾擠：傳 0 或負數會回到預設值，傳大於 500 會被夾到 500。
- **回應**是 `{ rows, nextCursor }`。其中 `nextCursor` 是一個 opaque（不透明）字串，由 Domain 的純函式把最後一列的排序鍵編碼而成。如果已經翻到底，`nextCursor` 會回 null，前端據此把「載入更多」隱藏起來。如果游標格式不符，handler 會回報參數錯誤，不會靜默處理。

之所以選 keyset 而不用 OFFSET/FETCH，原因是 OFFSET 分頁要資料庫先處理掉被略過的那些列再丟棄，略過得越多就越慢，而且在並發變動下會跳列或重複。keyset 則是用 `WHERE` 帶上「上一頁最後一列的鍵」來跳過，在有索引的情況下既高效、對並發又穩定（可參考 Microsoft Learn 的 EF Core Pagination 與 T-SQL ORDER BY OFFSET/FETCH 文件）。

**游標述詞一律寫成展開的布林式，不得用元組比較。** 多數資料庫（包含 SQLite）支援 row-value 元組寫法 `WHERE (k1,k2) > (@k1,@k2)`，但 SQL Server（T-SQL）不支援這種寫法。因此兩個 provider 共用展開的布林式：

```sql
-- 單鍵游標（entry_id / account_code / document_number）：
WHERE <key> > @cursor
-- 多鍵游標（若日後需 tiebreaker）：
WHERE k1 > @c1 OR (k1 = @c1 AND k2 > @c2)
```

每頁要取幾列，沿用各 provider 的方言，由 `ISqlDialect.LimitClause` 產出對應子句：SQLite 用 `LIMIT @pageSize`，SQL Server 用 `ORDER BY ... OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY`。要注意 SQL Server 這裡的 OFFSET 恆為 0，只是拿來做 TOP-N（且 FETCH 需要搭配 ORDER BY），並不是真的在做 offset 分頁。排序鍵必須唯一且有索引，否則翻頁會漏列或重複，而且 seek 會退化成全表掃描。各查詢的排序鍵安排如下：completeness 用既有的 account 索引、docBalance 用 doc 索引、其餘的用主鍵 `entry_id`；`result_filter_run` 則由它的 `(scenario_position, entry_id)` 主鍵天然覆蓋。

各查詢的「50 列以內預覽」語意，與「Page 走訪全量」的職責是分開的。舉例來說，completeness 的預覽維持 `ABS(diff) DESC`（差異最大的優先），而 Page 走全量時改用 `account_code` ASC（這個鍵可索引、又穩定）。隨機跳頁碼（也就是任意 offset 跳頁）不支援，實際上也不需要，因為底稿匯出與 GUI 審閱都只需要「下一頁」往前走。

### `result_filter_run`：已存篩選情境命中的行層落地

`filterHitsPage` 需要知道「某個情境命中了哪些 `entry_id`」。這些命中資料持久化在一張新表 `result_filter_run(scenario_position, entry_id, PK(scenario_position, entry_id))` 裡。這張表只存行層的參照，不存去正規化後的整列資料，這樣才能避免資料重複與失去同步。傳票層的資訊則由 distinct 的 `document_number` 推得。

- **建表**：跟著基底 schema 一起建立，SQLite 用 `IF NOT EXISTS`、SQL Server 用 `IF OBJECT_ID(...) IS NULL`，而且不升 schema 版本，這沿用 `app_message_log` 與 `gl_control_total` 的先例。
- **落地時機**：在 `filter.commit` 保存情境時，用既有的 `GlFilterWhereBuilder` 針對該情境的 AST 組出命中述詞，再把命中的 `entry_id` 寫入。寫入時會把該 `scenario_position` 先刪後插，所以是冪等的。
- **惰性補算**：`filterHitsPage` 在讀取時，如果發現該 position 還沒有落地的列（例如在這個機制上線之前就保存好的舊情境），就會即時用同一套述詞補算、落地之後再回傳。這是一次性的補算，做得很穩健（robust）。
- **失效不變量**：`result_filter_run` 屬於依當前母體算出來的衍生資料，所以也納入結果失效集合。當重投影清掉 `target_gl_entry` 時，會在 `RuleRunResultReset.ClearWithinAsync` 的同一個交易內一併執行 `DELETE FROM result_filter_run`（這跟 §2.5、以及 `gl_control_total` 是同一個模式）。這樣命中參照就永遠不會指向已經失效的母體，而且也不會破壞 `result_rule_run` 的回放。

### INF 60 行明細回取（infSamplePage）

INF 抽樣（見 §4）原本只回傳筆數與 seed，沒有一條路徑可以把明細讀回來。`infSamplePage` 補上了這條路徑：它讀取既有的 `result_inf_sampling_test_sample`（裡面是已經落地的抽樣 `entry_id`），join 回 `target_gl_entry`，並且限定只取最新一次 validate run 的樣本，以 `entry_id` ASC 做 keyset 分頁。它回傳的是 step2 攸關的欄位：傳票號、科目編號與名稱、借方金額與貸方金額（由帶正負號的 `amount_scaled` 拆成兩欄）、過帳日、核准日、編製人員、核准人員、摘要。金額以 scaled BIGINT 輸出，到顯示層再 ToDisplay 換算。這個查詢不會重新抽樣（因為 seed 已經固定並落地），它只負責把結果讀回來。

### 多情境 tag 矩陣（query.tagMatrix*；對應方法學 step4／step4-1）

事務所方法學的 step4「符合高風險條件傳票」（在傳票層用 C1 到 CN 表達命中與否的布林矩陣）與 step4-1「符合高風險條件傳票明細」（在行層逐行標記 C*_TAG），都需要一份矩陣，內容是「跑過全部已存情境之後，逐傳票、逐行標記出它命中了哪些情境位置」。子專案 D1 的 `filterHitsPage` 一次只能回單一情境的命中行，所以子專案 D2 補上了跨情境矩陣的查詢基礎設施。這份矩陣會餵給子專案 E（也就是 writer）使用；D2 本身只提供查詢，不做匯出。

| Page action | 資料 | 排序／游標鍵（唯一） | 列形狀 |
|:---|:---|:---|:---|
| `query.tagMatrixScenarios` | 矩陣表頭／step3 交叉參考：全部已存情境 + 命中數 | 依 `position` 升冪（非分頁） | `{ position, name, voucherHitCount, rowHitCount }` |
| `query.tagMatrixVoucherPage` | step4 傳票層：去重命中傳票 | `document_number` ASC | `{ documentNumber, postDate, createdBy, voucherTotal, matchedPositions:[int] }` |
| `query.tagMatrixRowPage` | step4-1 行層：命中傳票之**所有行**（含非命中行） | `entry_id` ASC | `{ documentNumber, lineItem, postDate, approvalDate, createdBy, approvedBy, accountCode, accountName, amount, matchedPositions:[int], description }` |

- **矩陣即時從 `result_filter_run` 算出，不落地一張新的矩陣表**。命中資料本來就已經落地在 `result_filter_run` 裡（行層的 `(scenario_position, entry_id)`），所以算矩陣（pivot）只是便宜的 `JOIN`、`GROUP BY` 與 keyset 查詢。`result_filter_run` 的行層結構同時導得出兩種矩陣：step4 的傳票層（某傳票命中情境 S，等價於該傳票有任一行在 `result_filter_run` 裡被標記為 S）與 step4-1 的行層（某行命中 S，等價於 `(S, 該行 entry_id)` 這筆存在）。如果另外存一張 pivot 矩陣表，就會造成資料重複、引入新的失效來源、還可能跟 `result_filter_run` 不一致。所以 D2 選擇即時計算，這樣它永遠和命中資料一致，也不會多出任何新的失效不變量要維護。分頁同樣沿用 D1 的 Page 原語（PageCursor、PageRequest、PageResult）做 keyset 分頁。
- **每頁採兩段查詢（為了 provider 中立、避開方言聚合）**。「每傳票或每行命中了哪些情境位置」是一個一對多的關係。如果要在單一 SQL 內把這些位置聚成一欄，就得用 `group_concat`（SQLite）或 `STRING_AGG`（SQL Server）這類各 provider 寫法不同的聚合函式。為了維持 provider 中立、也讓 keyset 分頁保持乾淨，改成每頁跑兩段查詢。第一段是實體頁查詢（keyset），取本頁去重後的傳票（以 `document_number` ASC 為鍵）或命中傳票的所有行（以 `entry_id` ASC 為鍵），連同核心顯示欄一起取，用 `ORDER BY 鍵` 加上 `Dialect.LimitClause`，回傳本頁的鍵範圍。第二段是位置查詢，針對同一個鍵範圍取出 `(實體鍵, scenario_position)`，再在 handler（C# 端）分組成每個實體的「命中位置有序去重清單」，也就是 `matchedPositions`。這兩段查詢都是參數綁定、純 ANSI（只有 LimitClause 走方言），而且每頁是有界的（最多 pageSize 個實體，每個實體最多 10 個位置）。至於把位置對映到 C1 到 CN 欄，那屬於子專案 E 的工作。
- **`voucherTotal` 等於該傳票的 `SUM(debit_amount_scaled)`**，也就是傳票的借方總額。scaled 換算成 decimal 的顯示由 handler 負責。這對齊樣本 step4「傳票總金額」是以毛額正數呈現的做法。row 矩陣裡的 `amount` 則是帶正負號的 scaled 值。非命中的行，其 `matchedPositions` 是一個空清單 `[]`。
- **輔助索引（只加不改、不升版）**。`result_filter_run` 的主鍵是 `(scenario_position, entry_id)`，`entry_id` 不是前導鍵，因此「以 entry_id join 回 `target_gl_entry` 算傳票」與「行頁的位置查詢 `WHERE entry_id` 範圍」都得不到最佳的索引。為此新增一個索引 `idx_result_filter_run_entry ON result_filter_run(entry_id, scenario_position)`（兩個 provider 都建，SQLite 用 `IF NOT EXISTS`、SQL Server 用 `IF ... IS NULL`，且不升 schema 版本，沿用前述只加不改的建表慣例）。
- **惰性 materialize（沿用 D1 的做法，並提取成共用服務）**。矩陣必須反映出全部已存的情境。這裡沿用 `filterHitsPage` 的惰性補算：首次查詢時如果結果是空的（或 summary 全為 0），而 `config_filter_scenario` 裡其實有定義，就重用 `IFilterRunMaterializer` 把全部情境落地之後，再重取一次。為了避免重複實作，把 `filterHitsPage` 原本私有的 `MaterializeAllAsync` 提取成一個共用的 Application 服務 `FilterRunMaterializeService`，由 `filterHitsPage` 與 D2 的三個 handler 共用（這是 DRY 原則，維持單一事實來源）。

### Schema 分層 (兩 Provider 共通)

| Schema | 內容 |
|:---|:---|
| `staging` | 原始匯入 (未處理) |
| `target` | 標準化後的 GL / TB / AccountMapping / DateDimension |
| `result` | 規則執行結果與彙總表 |
| `config` | 專案設定、欄位映射、規則參數 |

### Provider 等價測試

同時維護兩套方言是有成本的，這個維護成本必須用 golden tests 來支付，做法如下：

1. 同一組 `data/` fixture 同時在 SQLite 與 SQL Server 兩個 provider 上跑。
2. 每一條 V/R/Filter 規則，都比對 summary、resultRef 的 metadata、以及分頁過的明細列。
3. 金額的比對以 scaled integer 為準。
4. INF 抽樣則比對 seed 與抽中的 keys。
5. 如果公司環境不能跑容器化（containerized）的 SQL Server，就必須提供一個固定的整合測試資料庫，或一條明確的替代驗證路徑。

### DuckDB 的 Benchmark Gate

DuckDB 只能以候選 provider 的身分進入 benchmark，不能直接進正式架構。評估時至少要比較下列幾項：

| 項目 | 比較內容 |
|:---|:---|
| 正確性 | scaled integer / decimal 匯入、完整性測試、借貸不平測試、連續零尾數結果是否一致 |
| 效能 | 100 萬 / 1,000 萬 GL rows 下完整性測試、借貸不平測試、期末後核准、連續零尾數、filter.preview |
| 方言成本 | 與 SQL Server 的 SQL 差異是否少於 SQLite |
| 治理 | .NET client 維護狀態、授權、資安審查、部署方式 |

在通過上述這道 gate 之前，正式的本機 provider 仍然是 SQLite。

---

## 14. 專案結構規劃

下面是建議的起步目錄配置。JET 是單一一個 WinForms 專案，而資料夾的結構本身並不會強制執行 Clean Core，真正重要的是邊界規則。因此一開始只保留主要的邊界，不要預先建立過多的空子目錄。

```
src/JET/
├── JET.slnx
└── JET/
    ├── JET.csproj
    ├── Program.cs                  # Main + DI composition root
    ├── Form1.cs / Form1.Designer.cs # WebView2 host — 僅此而已
    ├── wwwroot/                    # 從 docs/jet-template.html 衍生的 HTML/CSS/JS
    │   ├── index.html
    │   ├── css/
    │   ├── js/
    │   │   ├── jet-api.js          # 唯一前端 JetApi action channel
    │   │   ├── state.js            # UI 暫態，不保存 GL/TB row population
    │   │   └── app.js              # bootstrap / event binding
    │   └── assets/
    ├── Bridge/
    ├── Application/
    ├── Domain/
    └── Infrastructure/

src/JET/tests/JET.Tests/            # xUnit
    ├── Domain/                     # Rule spec 單元測試 (純邏輯)
    ├── Application/                # Handler 測試 (mock repository)
    └── Infrastructure/             # 每個 Provider 的 integration / golden test
```

子目錄的拆分採延後原則：等到同一層的檔案超過 3 到 5 個、或已經出現明確的群組時，再去拆。舉例來說，等 `Application` 真的有了多個 command 或 query handler 之後，再建立 `Commands/`、`Queries/`、`Contracts/`；等 `Infrastructure` 開始同時實作 SQLite、SQL Server、檔案 I/O 與匯出之後，再建立 `Persistence/Sqlite/`、`Persistence/SqlServer/`、`FileIO/`、`Exporting/`。

---

## 15. 命名與分層原則

下列十二項原則是維護 JET 分層邊界與可測試性的基本約定，所有新增與修改的程式碼都適用：

1. **Form1 只當 host**：`Form1.cs` 內不碰業務邏輯，只負責初始化 WebView2 與 Bridge。
2. **前端只送 `action + payload`**：前端不拼 SQL，也不呼叫資料庫。
3. **Bridge 不做業務**：它只負責協定的解析與分派。
4. **每條規則一個 Handler**：不要寫成一個龐大的 `ProcessAllRulesCommand`。
5. **Command 與 Query 的 record 都是 `sealed record`**：這樣才不可變、容易序列化、也容易測試。
6. **Handler 透過建構子注入介面**：不要在裡面 new 具體的 class。
7. **Domain 沒有 I/O 依賴**：只要 Domain 裡出現 `System.IO`、`System.Data`、`Microsoft.Data.*`、`ClosedXml` 之類的 using，就是寫錯了。
8. **使用參數化查詢**：所有進到 SQL 的使用者輸入，一律走 `SqlParameter` 或 `SqliteParameter`，拒絕字串拼接。
9. **金額一律用 scaled integer**：資料庫 schema、規則 SQL、測試與匯出，全部都以 scaled 欄位為權威。
10. **測試金字塔加上 golden tests**：Domain 寫單元測試；Application 的 handler 用 mock repository 測試；Infrastructure 在兩個 provider 上做整合測試與 golden test。
11. **不要引入非必要的 mediator 或 mapper 套件**：不使用 MediatR 或 AutoMapper，改用手寫的 dispatcher 與明確的 DTO mapping。
12. **action 契約要穩定**：action 名稱與 payload schema 都納入文件管理，前端改版時不可以改動 action 契約。

### 15.1 業務規則語意的權威位置

- 業務規則的語意，由本文件 `docs/jet-guide.md` 的 RuleSpec、`Domain` 的 model 與 spec、以及 `Application` 的 use case 共同定義。
- `Infrastructure` 負責把這些語意轉成 SQLite 或 SQL Server 的參數化、set-based SQL。
- Repository 的實作不應該成為業務規則的事實來源。
- SQL 字串也不應該成為業務規則的唯一事實來源。如果 SQL 和 RuleSpec 的語意不一致，要先修規格與測試，再去修 SQL。
- 前端的 JavaScript 不得承載具權威性的業務規則，它只能顯示狀態、收集輸入、呼叫 `JetApi`，以及呈現 summary、preview、page、export 的結果。

### 15.2 資料存取與匯出原則

- 規則 SQL 以原始的參數化 SQL 為主，資料存取可以用 ADO.NET 或 Dapper 這類薄層，但不要用 EF Core LINQ 來表達 V/R/Filter 規則。
- 大量匯入（bulk import）由 provider 各自實作：SQL Server 用 `SqlBulkCopy`，SQLite 用 transaction 加 prepared statement batch。
- Schema migration 採用以 SQL script 為基礎的 migrator，不用 EF migrations 來管理本專案的主要資料庫 schema。
- 匯出明細使用 OpenXML SAX writer，ClosedXML 不得拿來當大型工作底稿明細的 writer。
- 所有耗時較長的匯入、查詢、匯出操作，都必須串接 `CancellationToken`。

---

# D. 開發與協作

## 16. AI-agent 開發工作流

JET 以 AI agent 為主要的開發助力，但這個助力只有在邊界與契約穩定的前提下才發揮得出生產力。本節說明工具鏈如何分工、穩定契約放在哪裡、AI 不該做哪些事，以及驗證流程與 UI 的 guardrails（防護欄）。

### 16.1 工具鏈分工

| 任務 | 工具 | 備註 |
|:---|:---|:---|
| 主場開發 (WinForms / WebView2 整合、Designer 相關) | **Visual Studio 2026 + Copilot Agent Mode** | 必須用 VS；Designer.cs 不讓外部 AI 動 |
| 跨檔重構、Rule Handler 批次生成 | Copilot Agent Mode / Codex CLI / Claude Code | 三選一都可；優先 Copilot (IDE 原生)，跨 repo 級時用 Codex / Claude Code |
| 單元測試生成 | Copilot / Claude Code | 依 RuleSpec + 小型 fixture 生成 |
| HTML/CSS/JS 前端 | Copilot / Claude Code | 但**不可改 action 契約**與 fixed binding ID |
| SQL 方言調整 | Copilot | SQLite ↔ SQL Server 方言差 |

目前這個 repo 的 AI 客製化檔案分工如下：

1. `AGENTS.md`：所有 agent 的短索引，以及不可違反的邊界。
2. `CLAUDE.md`：Claude Code 的入口，會指向 `AGENTS.md` 與相關文件。
3. `.claude/skills/minimalist-ui/`：Claude Code 的前端視覺設計 skill。
4. `.github/copilot-instructions.md`：GitHub Copilot 的 repository 層級規則。

如果未來真的需要針對特定路徑的 Copilot instructions、prompt files 或 custom agents，再去新增對應的 `.github/` 子目錄。不要預先建立空的資料夾，也不要引用根本不存在的設定檔。

### 16.2 讓 AI 提速的關鍵：**穩定契約**

AI 之所以能做得快，是因為邊界清楚；AI 之所以會做爛，是因為邊界模糊。

| 穩定邊界 | 放哪 |
|:---|:---|
| Action 名稱與 payload JSON schema | `docs/action-contract-manifest.md` |
| Frontend action contract / step data outline | `docs/action-contract-manifest.md` |
| RuleSpec 表 | 本檔第 5 節 |
| 欄位標準名稱 | 本檔第 18 節欄位對照 |
| Repository 介面 | `Domain/` 內的 contract；檔案量增加後再拆子目錄 |
| Frontend fixed binding ID | `docs/jet-template.html` / packaged `wwwroot/index.html` 的 `data-bind="*"` 屬性 |

建議拿 `AGENTS.md` 當跨工具的短索引，而 `CLAUDE.md`、`.claude/`、`.github/` 只放各工具專用的規則。不要把所有規則都塞進單一一大坨 instruction 裡。

### 16.3 AI 不該做的事

- 不可自行更改 action 名稱或 payload 欄位。
- 不可自行更改 RuleSpec 的語意；AI 只能補實作，不能改規則。
- 不可動 `Form1.Designer.cs` 與 `Form1.resx` 以外的 Designer 生成檔。
- 不可在 Application 層寫 provider 判斷（例如 `if (isSqlite) ...`），方言差異一律在 Infrastructure 處理。
- 不可引入 MediatR 或 AutoMapper 當預設解法。本專案是靠手寫的 dispatcher、明確的 command/query handler 與 DTO mapping 來控制授權與複雜度。
- 不可在 SQLite 用 `REAL` 或文字 decimal 聚合來做金額的權威計算。
- 不可用 ClosedXML 或 DOM workbook 來寫大型明細的工作底稿。
- 不可為了一次性的對話就新增零散的文件。如果需要持久保存 AI 的 context，優先去維護 `AGENTS.md`、`CLAUDE.md`、`docs/action-contract-manifest.md`、`.claude/skills/` 與 `.github/copilot-instructions.md`。

### 16.4 驗證與測試的 loop

AI 每次產出程式碼之後，都應該執行下面這組命令：
```
dotnet restore src/JET/JET.slnx
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

如果 agent 當下所在的環境不具備 .NET SDK、不支援 Windows Desktop targeting、或無法做 package restore，就應該明確回報哪些命令失敗了、哪些跳過了，不要假裝自己已經驗證過。

在 VS 2026 裡，Copilot Agent Mode 會自動跑這組命令；在 Claude Code 或 Codex CLI 裡，則是手動觸發、或由 agent 自己觸發。

### 16.5 UI/UX guardrails

JET UI 的目標，是讓審計員能清楚地完成案件建立、檔案匯入、欄位配對、科目配對、完整性測試、進階篩選與底稿輸出這一整套流程。它的優先順序是清楚、穩定、可追蹤，不追求華麗的效果。具體要求如下：

- 所有耗時較長的 action，都應該有 loading、busy、error、success 等狀態的呈現。
- 驗證、匯入、篩選的結果，都應該有一個讓人看得懂的 summary。
- 欄位配對的畫面，應該清楚區分來源欄（source column）、標準欄位（standard field）與配對狀態（mapping status）。
- 進階篩選的 UI，應該清楚呈現 AND 與 OR 的分組關係。
- 可點擊的元素，應該有明確的 affordance（讓人看出它可以點）。
- 鍵盤的 focus 狀態不應該被移除。
- 資料表格不應該一次載入完整的 GL/TB 列。
- 預覽、分頁、匯出都應該走後端控制的路徑（backend-controlled path）。
- UI 可以去改善互動體驗，但不能改變資料處理與業務規則的邊界。

不要加入行銷型 landing page 的版型、glassmorphism、neumorphism、cyberpunk 這類純風格化的設計，也不要引入大量的前端套件，或任何與 WinForms + WebView2 + 靜態 HTML 不相容的 UI stack。

---

## 17. 從 ideascript.bas 遷移的做法

遷移原則只有一句話：不要逐段翻譯。應該改採「聲明式規格加上重新實作」的做法，也就是一切以本文件的 RuleSpec 為準，而不是回頭去複製 VBA 的實作細節。

### 遷移四分類

| 類別 | 舊位置 (ideascript.bas) | 新位置 |
|:---|:---|:---|
| **Domain Rules** | 預篩選規則與自訂變體（legacy 代號 R1-R8 / A2-A4）的業務邏輯 | 本檔第 5 節 RuleSpec → `Domain/Rules/*.cs` |
| **Application Use Cases** | `Step1_Validation` / `Step2_*` / `Step3_Routines` / `Step4` / `Step5_*` | `Application/Commands/*` |
| **Infrastructure** | `Z_DirectExtractionTable` / `Z_renameFields` / `Z_Rename_DB` / 檔案 I/O | `Infrastructure/*` |
| **UI Workflow** | `Intro_Dlg` / `TBDetail_Dlg` / `GLDetail_Dlg` / `Criteria_Dlg` | `wwwroot/*` + action 綁定 |

### 實作優先序

1. **先鎖定資料相關的決策**：包括 schema 使用 scaled integer 金額、staging/target/result/config 的分層、resultRef，以及 keyset 分頁。
2. **Domain 先行**：把 5 個核心實體與 RuleSpec 寫成 C# 類別，並建立單元測試。
3. **建立 WebMessage Bridge 的最小閉環**：實作 `system.ping`，讓它回傳 host 狀態，藉此讓前端驗證 request/response 的 envelope。
4. **打通以檔案為基礎的 ImportGl 與 ImportTb**：把「檔案路徑 → reader streaming → staging → target」這條真實的資料流串接起來。
5. **加一條垂直的規則切片**：例如借貸不平測試或期末後核准，完整走過「Command/Query → Repository → SQL → result table → resultRef → 前端 summary/page」這一整條路。
6. **建立雙 provider 的 golden test**：用同一組 fixture 同時跑 SQLite 與 SQL Server，比對 scaled 金額、summary 與明細列。
7. **依 RuleSpec 量產其餘規則**：這部分 AI 可以批次生成，但每一條都必須通過 provider 的 golden test。
8. **實作進階篩選與工作底稿匯出**：filter.preview 與 filter.commit 走 resultRef，匯出則使用 OpenXML SAX writer。
9. **做 SQL Server 大資料量的強化**：補上 columnstore、大量匯入、query plan 與取消機制。
10. **做 DuckDB benchmark**：這只是當成候選 provider 的研究，不會阻塞 SQLite 與 SQL Server 這條主線。

### 舊程式 → 新結構對照 (摘要)

如果需要查舊的規則名稱與來源欄位，可以參考 [`legacy/README.md`](../legacy/README.md)。但同樣不要從舊程式逐段翻譯，一律以本文件的 RuleSpec 重新實作。

---

## 18. 欄位對照表

下表列出標準的 C# 欄位與舊系統（IDEA 與 VBA）欄位之間的對應關係，供匯入時的映射與後續的可追溯性參考。

### GL (General Ledger)

| 標準 (C#) | IDEA 原名 | VBA 名 |
|:---|:---|:---|
| `DocumentNumber` | 傳票號碼_JE | FLD_DOCUMENT_NUMBER |
| `LineItem` | 傳票文件項次_JE_S | FLD_LINE_ITEM |
| `Amount` | 傳票金額_JE | FLD_AMOUNT |
| `AccountCode` | 會計科目編號_JE | FLD_ACCOUNT_CODE |
| `AccountName` | 會計科目名稱_JE | FLD_ACCOUNT_NAME |
| `DocumentDescription` | 傳票摘要_JE | FLD_DOCUMENT_DESCRIPTION |
| `ApprovalDate` | 傳票核准日_JE | FLD_APPROVAL_DATE |
| `PostDate` | 總帳日期_JE | FLD_POST_DATE |
| `VoucherDate` | 傳票日期_JE | FLD_VOUCHER_DATE |
| `CreatedBy` | 傳票建立人員_JE | FLD_CREATED_BY |
| `ApprovedBy` | 傳票核准人員_JE | FLD_APPROVED_BY |
| `SourceModule` | 分錄來源模組_JE | FLD_SOURCE_MODULE |
| `IsManual` | 人工傳票否_JE_S | FLD_IS_MANUAL |
| `DebitAmount` | DEBIT_傳票金額_JE_T | FLD_DEBIT_AMOUNT |
| `CreditAmount` | CREDIT_傳票金額_JE_T | FLD_CREDIT_AMOUNT |
| `DrCr` | DEBIT_CREDIT_JE_T | FLD_DR_CR |

> **`LineItem`（傳票文件項次）的來源與補編規則。** 如果來源沒有提供這個欄位（也就是欄位配對沒有對應到 `lineID`），投影時會在同一個交易裡替每張傳票自動編號補齊，用的是 `ROW_NUMBER() OVER (PARTITION BY 傳票號 ORDER BY source_row_number)`。要注意這個補出來的值是衍生的顯示值，不是客戶的原始資料。反之，如果來源有對應這個欄位，就照來源的值逐字保留。這個值只用於確保資料集的完整性、以及底稿明細的排序，不得拿來當任何規則計算或抽樣的鍵；抽樣一律依 `source_row_number`。SQLite 與 SQL Server 的結果等價。

### TB (Trial Balance)

| 標準 (C#) | IDEA 原名 | VBA 名 |
|:---|:---|:---|
| `AccountCode` | 會計科目編號_TB | FLD_ACCOUNT_CODE |
| `AccountName` | 會計科目名稱_TB | FLD_ACCOUNT_NAME |
| `ChangeAmount` | 試算表變動金額_TB | FLD_CHANGE_AMOUNT |
| `OpeningBalance` | Opening_Balance_TB | FLD_OPENING_BALANCE |
| `ClosingBalance` | Ending_Balance_TB | FLD_CLOSING_BALANCE |
| `OpeningDebitBalance` | — | FLD_OPENING_DEBIT_BALANCE |
| `OpeningCreditBalance` | — | FLD_OPENING_CREDIT_BALANCE |
| `ClosingDebitBalance` | — | FLD_CLOSING_DEBIT_BALANCE |
| `ClosingCreditBalance` | — | FLD_CLOSING_CREDIT_BALANCE |
| `DebitAmount` | — | FLD_DEBIT_AMOUNT |
| `CreditAmount` | — | FLD_CREDIT_AMOUNT |

---

## 19. 術語對照

下表整理 JET 領域內的中英術語與簡要說明，作為跨文件用語時的一致基準。
| 中文 | English | 說明 |
|:---|:---|:---|
| 日記帳分錄 | Journal Entry (JE) | 會計系統的逐筆交易記錄 |
| 總帳 | General Ledger (GL) | 所有 JE 的匯總帳簿 |
| 試算表 | Trial Balance (TB) | 各科目期間餘額彙總 |
| 傳票 | Voucher / Document | 一組同時借貸的分錄集合 |
| 會計科目 | Account | 分類交易性質的編碼 |
| 借方 / 貸方 | Debit (Dr) / Credit (Cr) | 資產增加為借；負債收入增加為貸 |
| 過帳 | Posting | 將分錄記入總帳 |
| 完整性測試 | Completeness Test (`completeness_test`) | GL ↔ TB 勾稽 |
| 借貸不平測試 | Document Balance Test (`doc_balance_test`) | 逐傳票借貸平衡檢查 |
| INF 抽樣測試 | INF Sampling Test (`inf_sampling_test`) | 可重現抽樣驗證非財務欄位（INF = Information Produced by the Entity） |
| 空值紀錄測試 | Null Records Test (`null_records_test`) | 關鍵欄位空值檢查 |
| 預篩選 | Pre-Screening | 基於風險指標的自動篩選（規則名稱見 §4 命名登錄表） |
| 進階篩選 | Advanced Filtering | 組合條件得出最終測試母體 |
| 工作底稿 | Work Paper | 審計證據的書面記錄 |
| 管理階層凌駕 | Management Override | 管理層繞過內部控制 |
| 職能分離 | Segregation of Duties | 不同人執行不同職能 |
| 科目配對 | Account Mapping | 企業科目對應標準分類 |
| 母體 | Population | 測試範圍內的全部資料 |
| 實質性程序 | Substantive Procedure | 直接測試財報金額的審計程序 |

> **傳票號碼在不同地區與系統下的命名差異。** 同一個「傳票號碼」的概念，在不同會計系統裡的稱呼並不一樣。亞洲的會計系統常用 Voucher Number。SAP 這類 ERP 常用 Document Number，泛指原始單據的編號，涵蓋發票、收據、訂單等。西方的會計系統則常用 Journal Number，指的是日記帳分錄的識別碼。不論 PBC 來源欄位用的是哪一個名稱，欄位配對之後都一律映射到標準欄位 `DocumentNumber`（見 §2.1 與 §18 的對照表），而下游規則只認標準欄位。
