# KCT 小組進階篩選條件設計（獨立分組）

> **狀態：Phase 1（九條）已實作，待 GUI 驗收（2026-06-23）。** 九條 KCT 條件（A、C、D、E、F、G、H、I、J）已落地並通過自動化測試（全套件 993 個案例通過，含 SQL Server LocalDB parity）；GUI 目視驗收待 Windows 端進行（`docs/windows-handoff.md`）。權威現況已回寫到 `docs/jet-guide.md` §4/§6、`docs/action-contract-manifest.md` 的 Filter/Criteria 章節、`docs/development-status.md`、`docs/development-log.md`；本提案因此降為設計脈絡的快照。**條件 B 仍屬 Phase 2**：它需要一個目前沒有的獨立 BS/IS 科目分類維度，待 KCT 交付完整分類清單。
>
> 範圍：在 Step 4「進階條件篩選」新增一個獨立的「KCT 小組條件」分組，涵蓋 KCT 小組方法學清單的十條條件。下文以代號 A–J 指稱這十條；這套代號是 KCT 自有的條件編號，不是本專案的里程碑代號。
>
> **使用者已定之分岔（2026-06-23 對話）：**
> - **條件 B（借固定資產 PPE、不含在建，貸費用）本輪緩做。** 它需要目前系統沒有的科目分類（固定資產、費用，且 KCT 要依資產負債表／損益表分類 TB 科目），屬於資料模型擴充，待分類方案定案後另案處理（見〈條件 B：緩做與理由〉）。
> - **條件 A** 採「曆年四季底（3/31、6/30、9/30、12/31）前 X 天，並比對總帳（過帳）日 `post_date`」。
> - 本輪只交付「先出設計規格」一項；實作要等使用者核准本提案後另起一輪。
>
> **來源：** KCT 小組提供的十條方法學篩選清單（對話 2026-06-23）。各條所需的參數（天數 X、摘要關鍵字、人員姓名、尾數樣態）依清單註記，由查核員在**條件上輸入**，不寫死在程式裡。

## 決策紀錄（2026-06-23 使用者確認）

以下確認**取代**本提案各節原本標「待確認」的註記；其中條件 B 由「緩做」改為「納入」：

1. **E/F/G/I**：採「重用既有型別的 KCT 預設」（第一節的建議案）。
2. **條件 C 的一般對方科目集合**：不含現金（Cash），只含應收款項（Receivables）加預收款項（Receipt in advance）。
3. **條件 I 的比對日期**：只比對過帳日 `post_date`（即 `weekendPosting OR holidayPosting`）。
4. **條件 H 的尾數定義**：取主單位的整數尾數，不含小數。
5. **條件 A 的季別**：採曆年四季 Q1–Q4（季底為 3/31、6/30、9/30、12/31）；X 由查核員每次輸入。
6. **條件 B**：納入本案，採「新增 BS/IS 分類維度」（取消原本的「緩做」）。

**BS/IS 維度與節奏（2026-06-23 續確認）：**

- **承載方式**：用一個獨立的 BS/IS 分類匯入（新檔、新表），與科目配對表分離。
- **類別字彙**：要等 KCT 提供完整的 BS、IS 科目分類清單後，才能依表設計白名單與匯入格式——**Phase 2 因此卡在 KCT 交付分類清單**。
- **實作節奏**：分兩階段。Phase 1（本輪起跑）是已鎖定的九條（A、C、D、E、F、G、H、I、J）；Phase 2 是 BS/IS 獨立分類維度加上條件 B，待清單到位後另起，屆時再補完整設計並回寫第五、六、七節。

## 背景與動機

KCT 小組有一份固定的方法學篩選清單（A–J），要在 JET 的「進階條件篩選」（Step 4）裡實際跑。盤點後，這十條對應到三種落點：

- **既有條件就能表達**（空白摘要、特定摘要、特定人員、非營業日）：工具已有對應述詞，只缺一個讓 KCT 一鍵帶入的入口。
- **需新增述詞、但不阻塞**（季末前借記收入、收入無一般對方科目、特定金額尾數、編製等於核准同一人）：這些純讀既有的 `target_gl_entry`（部分另需科目配對），加一條參數化的集合式 SQL 述詞即可。
- **需先擴充資料模型**（借固定資產、貸費用）：需要一個新的科目分類維度，所以本輪緩做。

「獨立於其他類別」這個需求，落實為**前端的一個新條件分組** `KCT 小組條件`（`FILTER_RULE_GROUPS` 的第五組），與既有四組（風險預篩選訊號、依欄位內容、依分錄性質、進階樣態分析）並列、不混入。分組只是前端呈現；條件的 wire 型別與述詞各自獨立。

**不新增任何 bridge action。** 全部條件沿用既有的 `filter.preview` 與 `filter.commit` 契約，只擴充條件 AST 的型別與述詞——這跟先前「低頻科目升級」「自訂編製人員張數」走過的擴充路徑相同。

## 開發原則與邊界（harness）

- **分層**：`Bridge/Form1 → Application → Domain ← Infrastructure`。述詞、門檻、白名單放 Domain 或 Infrastructure；provider 分支只在 Infrastructure；Application 不引用 Infrastructure。
- **前端零商業邏輯**：不在前端、Form1、HTML、CSS、JS 做驗證、規則或 SQL；前端只組裝條件 AST 並呈現結果。
- **postMessage 邊界**：不在 `jet-api.js` 以外呼叫 `window.chrome.webview.postMessage`。
- **規則就是參數化的集合式 SQL**：SQL 識別字只能來自 `GlFieldWhitelist` 或本層常數；使用者輸入的值（天數、關鍵字、姓名、尾數、分類）一律以參數綁定，`DbCommand` 保持 provider 中立。
- **契約先行**：動 AST 的 wire 形狀前，先改 `docs/action-contract-manifest.md`（Filter / Criteria 章節新增五個型別加一個欄位）；由 `ActionContractTests` 鎖住回應形狀。
- **雙 provider 等價**：每條新述詞在 SQLite 與 SQL Server 兩側等價（以 `GlRulePredicates` 為單一事實來源，方言差異經 `ISqlDialect` 處理）；新述詞比照既有規則加 `[SqlServerFact]` parity。
- **沿用既有的結果失效不變量**：新述詞純讀 `target_gl_entry`（其中 C、A、D 另讀 `target_account_mapping`，I 另讀 `staging_calendar_raw_day`）；本版 filter 是無狀態查詢（`filter.preview` 不落地），不新增失效來源。
- **不編輯** WinForms designer 產生的檔。**不自行 commit**（版控由使用者親自下令）。

---

## 一、KCT 小組條件分組（前端呈現）

- 在 `ui-core.js` 的 `FILTER_RULE_GROUPS` 新增第五組 `{ key: 'kct', label: 'KCT 小組條件' }`，顯示在四個既有分組之後。
- 「快速加入」面板新增「KCT 小組條件」一列，列出本清單的九個按鈕（B 緩做）；型別下拉同步新增 `KCT 小組條件` optgroup。
- KCT 條件依落點分兩種實作（見下表「落點」欄）：
  - **新型別（self-contained）**：A、C、D、H、J。新增 AST 型別、掛 `group:'kct'`，可在型別下拉的 KCT optgroup 內直接選用。
  - **重用既有述詞的 KCT 預設（preset）**：E、F、G、I。KCT 區塊提供一鍵按鈕，帶入一條既有型別且預先填好的規則，這樣就不必為相同邏輯重造型別與述詞，維持單一事實。帶入後，該規則會以它原生的型別呈現在情境中。

> **設計取捨（核准時可改）**：E/F/G/I 採「預設帶入既有型別」而不是「另立 KCT 型別」，是為了不重複既有述詞（Good Taste：相同的 SQL 不要有兩個型別）。代價是帶入後該列顯示為原生型別（文字、自訂關鍵字、預篩選），而不是掛在 KCT 名下。如果 KCT 更看重「九條一律是 KCT 自有型別、UI 一致」，可改為四個薄型別（parser、validator、whereBuilder 各加一個 case，委派到既有述詞）；請於核准時指定。本提案建議用前者。

## 二、十條條件落點總覽

| 代號 | KCT 條件 | 落點 | 述詞 | 參數(查核員輸入) | 前置條件 |
|:--|:--|:--|:--|:--|:--|
| A | 季末前 X 天借記收入 | 🟠 新型別 | 季底日期視窗 ∧ 借記收入(Revenue 借方側) | 天數 X | 科目配對已匯入 |
| B | 借固定資產(不含在建)貸費用 | 🔴 緩做 | (需 PPE/費用分類) | — | (新分類維度) |
| C | 貸收入·借方非一般對方科目 | 🟠 新型別 | Revenue 貸方 ∧ 同傳票無「應收/預收」借方 | — | 科目配對已匯入 |
| D | 收入之人工分錄 | 🟠 新型別 | Revenue 列 ∧ `is_manual = 1` | — | 科目配對已匯入;`manual` 欄位已配對 |
| E | 特定人員建立之分錄 | 🟢 重用預設 | `text(createBy, 完全符合)` | 人員姓名(逗號分隔) | `createBy` 已配對 |
| F | 特定摘要(迴轉/調整…) | 🟢 重用預設 | `customKeywords`(摘要含) | 摘要關鍵字(逗號分隔) | — |
| G | 空白摘要 | 🟢 重用預設 | `prescreen: blankDescription` | — | — |
| H | 特定金額尾數(999999/000000…) | 🟠 新型別 | 顯示金額整數尾數符合指定樣態 | 尾數樣態(逗號分隔) | — |
| I | 非營業日分錄 | 🟢 重用預設 | `prescreen: weekendPosting OR holidayPosting` | — | 假日部分需假日曆已匯入 |
| J | 編製＝核准同一人 | 🟠 新型別 | `created_by = approved_by`(皆非空) | — | `createBy`、`approveBy` 已配對 |

圖例：🟢 重用既有述詞、🟠 新述詞（不阻塞）、🔴 需資料模型擴充。

---

## 三、新型別條件（A、C、D、H、J）詳設

以下所有 SQL 片段都以 `target_gl_entry` 的別名 `g` 表示，並沿用 `GlRulePredicates` 的既有風格：用 EXISTS 子查詢、用 `@p` 參數綁定、借貸側判定一律以 `amount_scaled >= 0` 為借方側、`< 0` 為貸方側（見 guide §6.1）。

### A. 季末前 X 天借記收入（`revenueDebitNearQuarterEnd`）

- **語意**：總帳（過帳）日 `post_date` 落在「任一曆年季底前 X 天的視窗」內，且該列為借記收入。季底指 MM-DD 屬於 {03-31, 06-30, 09-30, 12-31}；視窗為 `[季底 − X 天, 季底]`；借記收入指科目分類為 Revenue 且位於借方側（`amount_scaled >= 0`）。為什麼挑這條：借記收入本身就不尋常（收入正常應在貸方），而逼近季底的借記收入更是營收操縱的典型樣態。
- **視窗計算（在 Application/Domain 用純函式做，不進 SQL 識別字）**：由專案查核期間 `[periodStart, periodEnd]` 與 X，枚舉出與期間有交集的各個季底，算出每個 `[季底 − X, 季底]` 視窗（ISO `yyyy-MM-dd` 格式），再交給述詞以 OR 串接、邊界值用參數綁定。這裡之所以取視窗與期間的交集，只是為了避免產生「在期間外、永遠不會命中」的多餘子句，並不改變命中集。
- **述詞**（Revenue 借方，且視窗以 OR 串）：
  ```sql
  (EXISTS (SELECT 1 FROM target_account_mapping m
           WHERE m.account_code = g.account_code AND m.standardized_category = @revenue)
   AND g.amount_scaled >= 0
   AND ( (g.post_date >= @w1from AND g.post_date <= @w1to)
      OR (g.post_date >= @w2from AND g.post_date <= @w2to)
      OR ... ))
  ```
- **AST**：新型別 `revenueDebitNearQuarterEnd`；新增欄位 `WindowDays`（`int?`，即 X）。`requiresAccountMapping: true`。
- **前置與驗證**：科目配對須已匯入（否則回 `invalid_scenario`，鏡射 `accountPair`）；`WindowDays` 須為 1–92 的整數（超過一季的長度沒有意義）。
- **待確認**：X 沒有預設值，要求查核員填寫（清單註記「請提供天數 X」）。「季底」固定採曆年四季；若 KCT 指的財務季別與曆年不同，則另議。

### C. 貸收入但借方非一般對方科目（`revenueWithoutNormalCounterpart`）

- **語意**：本列為收入貸方（分類為 Revenue 且 `amount_scaled < 0`），但它所在的那張傳票，沒有任何一筆是「借方側（`amount_scaled >= 0`）且分類屬於一般銷售對方科目」。一般對方科目指應收款項（Receivables，含應收票據與應收帳款）加預收款項（Receipt in advance，含預收貨款與合約負債）。這條是「未預期借貸組合」（`unexpectedAccountPair`）的否定面：前者抓的是「收入貸加上有對方科目借」，本條抓的是「收入貸但缺對方科目借」。
- **述詞**：
  ```sql
  (EXISTS (SELECT 1 FROM target_account_mapping m
           WHERE m.account_code = g.account_code AND m.standardized_category = @revenue)
   AND g.amount_scaled < 0
   AND NOT EXISTS (
       SELECT 1 FROM target_gl_entry d
       JOIN target_account_mapping md ON md.account_code = d.account_code
       WHERE d.document_number = g.document_number
         AND d.amount_scaled >= 0
         AND md.standardized_category IN (@receivables, @receiptInAdvance)))
  ```
- **AST**：新型別 `revenueWithoutNormalCounterpart`；無參數。`requiresAccountMapping: true`。
- **前置與驗證**：科目配對須已匯入。
- **待確認**：一般對方科目集合是否要納入現金（Cash）？清單 C 原文只列「應收票據／帳款、預收貨款／合約負債」，所以本設計預設不含 Cash（也就是說，連現金收款也會被視為缺對方科目而命中）。既有 `unexpectedAccountPair` 的對方集合是含 Cash 的；若 KCT 要與其一致，把 `Cash` 加進上式的 IN 清單即可。請核准時指定。

### D. 收入之人工分錄（`manualRevenueEntry`）

- **語意**：科目分類為 Revenue 的分錄，且為人工分錄（`is_manual = 1`）。
- **述詞**：
  ```sql
  (EXISTS (SELECT 1 FROM target_account_mapping m
           WHERE m.account_code = g.account_code AND m.standardized_category = @revenue)
   AND g.is_manual = 1)
  ```
- **AST**：新型別 `manualRevenueEntry`；無參數。`requiresAccountMapping: true`。
- **人工與自動如何區分（清單 D 註記要求說明）**：沿用既有 `manualAuto` 條件的判定方式——以 GL 配對鍵 `manual`（人工或自動分錄）所對應的來源欄，投影成 `is_manual`（1 為人工、0 為自動）；來源未提供旗標（`is_manual` 為 NULL）的列永不命中（與既有 `manualAuto` 一致）。至於「明細帳需含傳票建立人員」一事，屬於匯出底稿的欄位，不影響本述詞。
- **前置與驗證**：科目配對須已匯入；`manual` 欄位未配對時，此條零命中（前端可提示）。

### H. 特定金額尾數（`trailingDigits`）

- **語意**：顯示金額（主單位的整數部分）的末 k 位，符合指定的尾數樣態，例如 `999999` 或 `000000`。可一次給多組樣態，任一符合即命中。
- **述詞**（對每個樣態字串 s：k 為長度、tail 為數值；`@scale` 為專案的 `MoneyScale`、`@tenk` 為 10^k）：
  ```sql
  (g.amount_scaled <> 0 AND (ABS(g.amount_scaled) / @scale) % @tenk = @tail)   -- 多組以 OR 串接
  ```
  先用整數除法 `/ @scale` 取主單位整數（捨去小數位），再對 `10^k` 取模來比對。SQLite 與 SQL Server 的整數 `/` 和 `%` 都是整數運算，所以雙 provider 等價（於 `SqlDialectTests` 與 parity 測試驗證）。
- **與既有 `customTrailingZeros` 的關係**：`customTrailingZeros` 只做「連續零尾數」（在 scaled 空間取模），而 H 一般化到任意尾數樣態（含 0 與 9）。兩者並存：`customTrailingZeros` 給「連續零」這個高頻情況一個乾淨入口，H 則滿足 KCT 的任意尾數需求。
- **AST**：新型別 `trailingDigits`；重用既有的 `Keywords` 欄位承載樣態清單（逗號分隔，每組限純數字）。不新增欄位。
- **驗證**：至少要有一組樣態；每組為 1 到 N 位純數字（N 取「使 `@scale × 10^k` 不溢位 `long`」的上限，沿用 `TrailingZeroThreshold` 的溢位防線推導）。
- **待確認**：H 以主單位的整數尾數定義（捨去小數、角、分）；若 KCT 的「尾數」指的是含小數位的尾數，定義就需調整（請告知須測試的尾數、以及是否含小數）。

### J. 編製與核准同一人（`preparerEqualsApprover`）

- **語意**：傳票的建立人員與核准人員是同一人（兩者皆非空白且相等）——這是職能未分離的紅旗。
- **述詞**：
  ```sql
  (g.created_by IS NOT NULL AND TRIM(g.created_by) <> ''
   AND g.approved_by IS NOT NULL
   AND UPPER(TRIM(g.created_by)) = UPPER(TRIM(g.approved_by)))
  ```
  純 ANSI 的欄位比較，雙 provider 相同。
- **AST**：新型別 `preparerEqualsApprover`；無參數。
- **前置與驗證**：`createBy` 與 `approveBy` 都須已配對（任一未配對，對應欄就是 NULL，該條零命中；前端可在兩者皆配對時才提供）。清單 J 註記「明細帳須提供建立人員與核准人員」指的就是這兩欄。

---

## 四、重用既有述詞的 KCT 預設（E、F、G、I）

這四條由 KCT 區塊提供一鍵按鈕，帶入既有型別且預填好的規則；**不新增述詞或型別**。

| 代號 | KCT 按鈕帶入 | 既有型別/鍵 | 預填 |
|:--|:--|:--|:--|
| E | 特定人員建立之分錄 | `text`,欄位 `createBy`,模式 `完全符合(exact)` | `keywords` = 查核員輸入的人員姓名(逗號分隔) |
| F | 特定摘要 | `customKeywords` | `keywords` = 查核員輸入的摘要關鍵字(預設提示「迴轉, 調整」) |
| G | 空白摘要 | `prescreen`,`prescreenKey = blankDescription` | 無 |
| I | 非營業日分錄 | 一個群組:`prescreen weekendPosting` **OR** `prescreen holidayPosting`(皆比對 `post_date`) | 無 |

- **E**：清單註記「須告知須測試人員之姓名」，所以姓名由查核員輸入。模式預設為完全符合，以免姓名被當作子字串而誤命中；若需模糊比對，改為「包含」。
- **F**：清單註記「請告知所需摘要內容」，所以關鍵字由查核員輸入；述詞同 `suspicious_keywords`（摘要含任一關鍵字、不分大小寫）。
- **G**：`blank_description` 是列述詞，逐列判定「該項次摘要空白」，正好對應清單 G 的「傳票中某一項次摘要為空白」。
- **I**：非營業日的定義是「週末（內建已排除補班日）或已匯入假日曆的日子」。清單註記「除臺灣及中國大陸外，其餘地區需提供非營業日之日期」，對應的就是假日曆匯入；假日曆未匯入時，只有週末部分會命中。**待確認**：比對的日期欄預設用過帳日；若 KCT 另要核准日，可加帶 `weekendApproval OR holidayApproval`。

---

## 五、條件 B：緩做與理由

- **清單 B**：借固定資產（PPE，不含在建工程）且貸費用的分錄。
- **阻塞點**：目前的科目分類是固定五類 `Revenue / Receivables / Cash / Receipt in advance / Others`，而且由資料庫的 `CHECK` 約束鎖死（[JetProjectDatabase.cs:163](src/JET/JET/Infrastructure/JetProjectDatabase.cs#L163)、[SqlServerProjectDatabase.cs:281](src/JET/JET/Infrastructure/SqlServerProjectDatabase.cs#L281)）。B 需要「固定資產（且能排除在建工程）」與「費用」這兩個分類，而且 KCT 表明要依資產負債表／損益表（BS/IS）為 TB 科目分類——這是一個目前不存在的分類維度。
- **本輪決策**：緩做。要等以下其一定案後再另案實作：（1）新增一個獨立的 BS/IS 科目分類維度（新匯入、新欄位，與現有五類並存）；或（2）擴充 `standardized_category` 白名單（改 CHECK、改匯入投影、改 UI）。一旦分類就緒，B 的述詞就和既有 `accountPair` 的 `exact` 模式同形（同一傳票，借方分類為固定資產且貸方分類為費用），工程量小；真正的成本在分類維度本身。
- **連帶說明**：C 在語意上雖然也涉及 BS/IS，但它用到的「應收／預收」已在現有五類內，所以 C 不被此阻塞、本輪可做。

---

## 六、契約與命名（落地時）

### 命名登錄表（guide §4）新增列

| slug | wire key | 中文名 | 類別 | 歷史代號 |
|:--|:--|:--|:--|:--|
| `revenue_debit_near_quarter_end` | `revenueDebitNearQuarterEnd` | 季末前借記收入 | 進階篩選條件(KCT) | — |
| `revenue_without_normal_counterpart` | `revenueWithoutNormalCounterpart` | 收入無一般對方科目 | 進階篩選條件(KCT) | — |
| `manual_revenue_entry` | `manualRevenueEntry` | 收入之人工分錄 | 進階篩選條件(KCT) | — |
| `trailing_digits` | `trailingDigits` | 特定金額尾數 | 進階篩選條件(KCT) | — |
| `preparer_equals_approver` | `preparerEqualsApprover` | 編製與核准同一人 | 進階篩選條件(KCT) | — |

（E/F/G/I 重用既有型別，不新增登錄列。）

### action-contract-manifest.md（Filter / Criteria）新增

- AST 的 `type` 列舉新增上述五個 wire key。
- AST 新增欄位 `windowDays`（整數，只有 `revenueDebitNearQuarterEnd` 用，即 X）。
- AST 的語意章節補上各新型別的述詞與前置條件；其中 `revenueDebitNearQuarterEnd`、`revenueWithoutNormalCounterpart`、`manualRevenueEntry` 標註「需科目配對已匯入」。
- `invalid_scenario` 的錯誤說明補上：新型別缺前置（科目配對）、`windowDays` 超界、`trailingDigits` 樣態非數字或缺漏。
- 在語意章節註明 H 重用 `keywords` 欄位來承載尾數樣態這件事（同一個欄、依型別取不同意義，鏡射 `maxEntries` 由兩個型別共用的做法）。

## 七、受影響檔案（落地時，待核准後）

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:五型別 + `windowDays` 欄位 + 語意/錯誤說明 |
| `docs/jet-guide.md` §4/§6 | 五條登錄列;§6 補各述詞邏輯;§6 註明 KCT 分組 |
| `Domain/FilterScenario.cs` | `FilterRuleType` 加五值;`FilterRuleSpec` 加 `WindowDays`;`FilterScenarioValidator` 五個 case |
| `Domain/RuleCatalog.cs` | 五條 KCT 進階篩選條件登錄(若 catalog 涵蓋 filter type) |
| `Application/FilterScenarioPayloadParser.cs` | 五型別解析;`windowDays`;`trailingDigits` 取 `keywords` |
| `Application/`(視窗計算) | A 的季底視窗純函式(期間 + X → 日期視窗清單) |
| `Infrastructure/GlRulePredicates.cs` | 新述詞:Revenue 列/借方側、季底視窗、收入無對方科目、人工收入、尾數樣態、編製＝核准 |
| `Infrastructure/GlFilterWhereBuilder.cs` | 五型別 `BuildRule` case |
| `wwwroot/js/ui-core.js` | `FILTER_RULE_GROUPS` 加 `kct`;`FILTER_RULE_TYPES` 加五型別(`group:'kct'`,A/C/D 標 `requiresAccountMapping`) |
| `wwwroot/js/steps/filter-step.js` | 五型別的 `ruleControlsHtml`／摘要;E/F/G/I 的 KCT 預設帶入按鈕 |
| `wwwroot/css/app.css` | KCT 分組沿用既有 `.quick-add__group` 樣式,原則上不需新類別 |
| `docs/development-status.md` / `development-log.md` / `windows-handoff.md` | 落地回寫 + 待驗卡 |

## 八、測試計畫（落地時，三層，TDD）

- **Domain**：`FilterRuleType` 的五值解析；`FilterScenarioValidator` 各 case 的 BVA 邊界值分析（`windowDays` 1–92、尾數樣態為純數字、前置條件）；A 的季底視窗純函式（期間與 X 推出視窗清單，含跨年度、X 大於季長度的邊界）。
- **Application 驗收**（`HandlerTestHost`，對著 manifest 的 wire）：每條新述詞的「命中數等於以 demo 母體獨立重算」；C 要對「有對方科目」與「無對方科目」的傳票各驗一次；A 對視窗邊界日各驗；H 對 `999999`、`000000`、含小數金額各驗；J 對同名、異名、空白各驗；前置條件缺失時回 `invalid_scenario`。
- **Infrastructure/parity**：每條新述詞在 SQLite 與 SQL Server 等價（`[SqlServerFact]`，LocalDB 閘控、可 clean skip），特別涵蓋 H 的整數除法與取模、以及 A 的多視窗 OR。
- **前端**：不寫 JS 業務規則測試；KCT 分組與各條件呈現列入 `windows-handoff.md`。

## 九、驗證指令（落地時）

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需要本機 LocalDB 並設定 `JET_SQLSERVER_CONNECTION`（閘控）。

## 十、待核准前需 KCT／使用者確認的點

1. **E/F/G/I 的實作形式**：建議用「重用既有型別的 KCT 預設」（本提案預設）；或改成「四個薄 KCT 型別」以求 UI 一致。
2. **條件 C 的一般對方科目集合**：是否納入現金（Cash）？（本提案預設不含，依清單原文。）
3. **條件 I 的比對日期**：只比過帳日（本提案預設），或同時含核准日。
4. **條件 H 的尾數定義**：主單位整數尾數（本提案預設，捨小數），或含小數位。
5. **條件 A 的 X 與季別**：X 由查核員每次輸入（無預設）；「季底」固定採曆年四季——若 KCT 用非曆年的財務季，需調整。
6. **條件 B 的科目分類方案**：新增獨立的 BS/IS 分類維度，或擴充現有白名單——定案後 B 再另案實作。

## 十一、交付狀態

- **設計階段**：本提案檔（已核准）。
- **Phase 1 實作（2026-06-23，已落地、待 GUI 驗收）**：九條 KCT 條件（A、C、D、E、F、G、H、I、J）。涵蓋契約（manifest 的 Filter/Criteria 加 guide §4/§6.2/§6.3）、Domain（`FilterRuleType` 五值加 `FilterRuleSpec.WindowDays` 加 `QuarterEndWindows` 純函式加 validator）、Infrastructure（`GlRulePredicates` 五述詞加 `GlFilterWhereBuilder`）、Application（`FilterScenarioPayloadParser`）、前端（`ui-core.js` 的 `kct` 分組、五型別、`FILTER_KCT_PRESETS`，以及 `filter-step.js` 的控制項、摘要、預設帶入）、三層測試（`QuarterEndWindowsTests`、`FilterScenarioValidatorTests`、`KctFilterPredicateTests`、`FilterHandlersTests`）。全套件 993 個案例通過；尚未 commit（版控由使用者決定）。
- **Phase 2（未動工）**：條件 B 加上獨立的 BS/IS 科目分類維度，待 KCT 交付完整的 BS、IS 分類清單後另起。
