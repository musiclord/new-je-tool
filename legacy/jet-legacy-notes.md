# JET 舊版筆記歸檔（IDEA / VBA 時期）

> **這是歷史歸檔,僅供業務語意與設計意圖對照,不是現行指引。** 內容來自上一代 JET 的開發筆記(Caseware IDEA + IDEAScript、Excel VBA + Access 時期)。受查者相關的識別資訊與其他不宜入版控的內容**一律已移除,或以佔位符代替**(`〈受查者〉`、`〈path〉`、`〈會計科目〉`、`〈金額〉`、`〈傳票號碼〉`)。
>
> 現行權威一律以 `docs/jet-guide.md`、`docs/action-contract-manifest.md`、`docs/jet-frontend-description.md` 為準;棄用原因、VBA 分層與 IDEAScript 函式地圖見 `legacy/README.md`。**不要逐行把這裡的腳本翻成 C#**——它的價值在於(1)保存審計方法論與設計動機,(2)作為遷移時的 golden-master / 差異測試對照基準(見 `.claude/skills/jet-testing/SKILL.md` §5)。引用的 R1–R8 / V/A 等為歷史代號,僅供回查,對應現行具體名稱見 `docs/jet-guide.md` §4 登錄表「歷史代號」欄。

---

## 1. 審計方法論:工作底稿五段結構（ISA / KAEG）

舊版以一份 Excel 工作底稿(`WorkingPaper.xlsx`)呈現整套會計分錄測試,分頁依審計實務排成五段。此結構是現行 Step 5 工作底稿匯出的**設計目標**(現行為雛形,見 `docs/jet-guide.md` §3)。以下為**通用欄位標題**,資料一律佔位符。

### Step 1 — 母體完整性 + 借貸不平 + 編製者

- **完整性測試**:逐科目比對「試算表本期變動金額(A)」與「該科目於會計分錄之本期借貸彙總(C)」,差異 = (A) − (C);`ABS(差異) > 0` 的科目須於 Step 1-3 說明調節。
  - 欄位:`試算表科目編號 | 試算表科目名稱 | 變動金額(A) | 會計分錄本期借貸彙總(C) | 差異`
  - 範例列:`〈會計科目〉 | 〔銀存〕支票存款 | 〈金額〉 | 〈金額〉 | 0`
- **Step 1-1 借貸不平測試**:對每一傳票號碼小計,確認是否有借貸餘額不為 0;若個別不平但整個母體餘額為 0,需向受查者確認原因。
- **Step 1-2 分錄編製人員說明**:彙總編製人員清單與其經手傳票數/金額,說明各人員是否適當(對應現行 `creator_summary`)。
  - 欄位:`編製人員 | 自動或人工 | 經手傳票數 | 經手傳票金額彙總 | 部門 | 職稱 | 是否適當之說明`
- **Step 1-3 完整性差異說明 + 調節**:對差異科目檢視相關傳票明細、說明造成差異的原因與調節,使差異視為 0 或保留剩餘差異說明。

### Step 2 — RDE 可靠性測試

依 ISA 240 / ISA 330(KAEG):納入高風險條件(HRC)的欄位即攸關資料元素(RDE),須先確認可靠性(完整性 + 正確性)才執行高風險篩選;預篩選(風險評估)階段則不需先確認。完整性多已於 Step 1 完成,此處測正確性,依屬性抽樣表選樣核對傳票附件。

- **財務類 RDE**(會計科目編號/名稱、借貸別、金額):
  - 程序 A — 確認是否已於完整性測試完成;否則核金額至附件、其餘核至已核准的會計科目表。
- **非財務類 RDE**:
  - 程序 B — 過帳日期/時間:內部交易核至核准的內部交易日期,外部交易核對交易憑證日期。
  - 程序 C — 傳票建立日期/分錄時間:依案件與樣本選一或多項程序驗證。
  - 程序 D–G — 其餘非財務欄位(摘要、人工/自動註記、編製/核准人員等)的核對(視 HRC 實際納入的欄位而定;未納入者該程序設 N/A)。
- 對應現行:工具內以 `inf_sampling_test`(INF 抽樣)提供可重現抽樣機制(見 `docs/jet-guide.md` §4)。

### Step 3 — 高風險範圍條件（HRC）彙總

辨識管理階層凌駕控制風險相關的分錄類型,彙總高風險範圍條件。

- 欄位:`條件 | 選擇此篩選條件的原因 | 符合條件之傳票數`
- 條件範例(通用,對應現行 §4/§5 規則):未預期借貸組合(`unexpected_account_pair`)、特定編製/核准人員(`creator_summary` 衍生)、週末/假日過帳且排除補班日(`weekend_posting`/`holiday_posting`)、人工分錄、摘要關鍵字(`suspicious_keywords`)。

### Step 4 — 符合高風險條件之分錄測試

- 主表欄位:`編號 | 傳票號碼 | 總帳日期 | 編製者 | 傳票總金額`
- 明細表欄位:`傳票號碼 | 傳票文件項次 | 總帳日期 | 編製人員 | 核准人員 | 會計科目編號 | 會計科目名稱 | 傳票金額 | 傳票摘要 | 各條件 tag 欄`
- 測試程序:核至複核紀錄確認核准、核至附件確認內容與會計期間、詢問編製細節。

### Step 5 — 財務報表關帳後調整之分錄

記錄關帳後入帳的調整分錄(post-closing entries)或未入帳直接調整(other adjustments),或說明無此類情形。最後將底稿納入 eAudIT。

### 附:輔助檔案欄位（配對前 → 配對後,通用）

- **TB**:`K_科目代碼(文字) → 會計科目編號_TB`、`K_科目名稱(文字) → 會計科目名稱_TB`、`K_本期變動數(數字) → 試算表變動金額_TB`。
- **GL**:`K_傳票號碼(文字) → 傳票號碼_JE`、`K_項次(數字) → 傳票文件項次_JE_S`、`金額欄位＋借貸判斷 → 傳票金額_JE`。
- **假期假日**:`DAYOFWEEK | WORKDAY(Y/N)`、`DATE_OF_HOLIDAY | HOLIDAY_NAME | IS_HOLIDAY`、`DATE_OF_MAKEUPDAY | MAKEUPDAY_DESC`(對應現行 DateDimension,`docs/jet-guide.md` §2.4)。
- **科目配對**:`GL_NUMBER | GL_NAME | STANDARDIZED_ACCOUNT_NAME`(對應現行 AccountMapping,§2.3)。

---

## 2. IDEA JE 資料管道（IDEAScript,已清洗）

舊版以 IDEAScript 串起 JE 母體的建置。保留通用 ERP 欄位名(`POST_DR`、`POST_CR`、`EFFECTDATE`、`POST_DATE`、`GL_ACCOUNT`、`UNIQUE`…)作為 schema 對照;路徑/客戶/科目/金額/門檻已清洗。可作差異測試的 ground-truth 對照。

管道(由 `JE.IDM` 起,輸出至 `〈path〉`):

1. **匯入**:`ImportUTF8DelimFile`(來源 `JE.csv`,受查者 `〈受查者〉`)→ `JE.IDM`。
2. **新增虛擬欄位**:
   - `K_傳票編號 = @split(UNIQUE, "-", 1)`(自 `UNIQUE` 切出傳票號)。
   - `K_項次`(整數,預設 0)。
   - 借/貸方特定科目旗標:`@if(@match(GL_ACCOUNT, "〈會計科目〉") .AND. POST_DR >= 〈金額〉, 1, 0)`;以及 `@if(@match(@mid(GL_ACCOUNT, 4, 4), 〈特定現金/應收科目片段清單〉) .AND. POST_DR <> 0, 1, 0)`。
3. **索引**:`K_傳票編號`、`GL_ACCOUNT`。
4. **Join 科目配對**:`Sum_科目配對.IDM` by `K_傳票編號`,帶入科目配對情境欄。
5. **期間擷取**:`@BetweenDate(EFFECTDATE, "〈期初〉", "〈期末〉")` → 期間內 GL。
6. **標準化欄名 + 衍生金額**:
   - `K_傳票編號 → 傳票號碼_JE`、`GL_ACCOUNT → 會計科目編號_JE`、`ACCOUNT_DESCRIPTION → 會計科目名稱_JE`、`DESCRIPTION → 傳票摘要_JE`、`EFFECTDATE → 總帳日期_JE`、`POST_DATE → 傳票核准日_JE`、`CREATEDUSER → 傳票建立人員_JE`、`POSTUSER → 傳票核准人員_JE`、`K_項次 → 傳票文件項次_JE_S`。
   - `傳票金額_JE = POST_DR - POST_CR`(借貸淨額)。
   - `DEBIT_傳票金額_JE_T = @if(傳票金額_JE >= 0, 傳票金額_JE, 0)`、`CREDIT_傳票金額_JE_T = @if(傳票金額_JE < 0, 傳票金額_JE, 0)`、`DEBIT_CREDIT_JE_T = @if(傳票金額_JE >= 0, "DEBIT", "CREDIT")`。
7. **Join 預篩選結果**:以傳票號碼/日期 join 傳票層彙總(帶入各預篩選 tag),再以傳票號碼/項次 join 分錄層彙總(帶入明細 tag)。
8. **輸出檔命名(歷史)**:`#GL#.IDM`(期間內標準化 GL)、`#GL#In_Period_Doc_Sum`(傳票層合計/不平清單)、`#GL_Account_Sum`(會科層期間合計)、`#Completeness_calculate`(GL/TB 合併＋差異)、`#Completeness_Check`(`ABS(差異)>0` 科目)等。

**對應現行架構**:標準化欄名 → `docs/jet-guide.md` §18 對照表;借貸淨額與 DEBIT/CREDIT 拆分 → §1.5.3 / §2.1;科目配對 → §2.3 與 `unexpected_account_pair`(§6.1);期間擷取 → `period_in_out`。**現行規則一律以參數化集合式 SQL 執行(guide §1.5.2),不重現 IDEAScript 的逐欄虛擬欄位作法。**

---

## 3. Access SQL 完整性實作（已清洗路徑）

舊版在 Access 端以 SQL 做完整性(GL ↔ TB 勾稽),可作現行 `completeness_test` 的差異(differential)oracle 對照。

- 跨庫載入 TB / JE(來源 `〈path〉`),期間篩選為 `JE_IN_PERIOD`(`WHERE [EffectDate] Between #〈期初〉# And #〈期末〉#`)。
- 新增並計算 `K_AMOUNT = Nz([Post Dr], 0) - Nz([Post Cr], 0)`。
- 依科目彙總:`SELECT [GL Account], SUM([K_AMOUNT]) AS K_AMOUNT_SUM INTO JE_ACCOUNT_SUM ... GROUP BY [GL Account]`。
- 外部連結:`JE_ACCOUNT_SUM LEFT JOIN TB ON [GL Account]=[GLCODE]` UNION `RIGHT JOIN`(模擬 FULL OUTER)→ `COMPLETENESS_CALCULATED`。

**對應現行**:`docs/jet-guide.md` §4 完整性測試(`FULL OUTER JOIN` GL_Sum_ByAccount × TB ON AccountCode;差異 = `TB.ChangeAmountScaled - GL.AmountScaledSum`)。**注意 Access SQL 方言與 SQLite / SQL Server 不同**(見 `legacy/README.md`)。

---

## 4. 日期維度篩選 SQL 樣式（已清洗）

舊版以一張 `DATE_DIMENSION` 表承載日期屬性,篩選以子查詢 `IN` / `NOT IN` 組成。對應現行 DateDimension(`IsWeekend` / `IsHoliday` / `IsMakeupDay`,`docs/jet-guide.md` §2.4)與週末/假日規則(補班日排除,§5)。

- 總帳/核准日期在週末:`CDate([PostDate]) IN (SELECT [DateKey] FROM DATE_DIMENSION WHERE [IsWeekend] = True)`。
- 在國定假日:`... WHERE [IsHolidays] = True`。
- **排除補班日**:`CDate([PostDate]) NOT IN (SELECT [DateKey] FROM DATE_DIMENSION WHERE [IsMakeupDays] = True)`。
- 僅借方 / 僅貸方:`WHERE [DrCr] = "D"` / `<> "D"`。
- 人工編制:`WHERE [IsManual] = True`。
- 文字 / 日期區間 / 數值區間:`LIKE '*keyword*'` / `BETWEEN #起# AND #迄#` / `BETWEEN 起 AND 迄`。

---

## 5. VBA QueryBuilder 設計意圖（現行 AST Query Builder 的前身）

舊版 `ServiceFilter` 內以 `ServiceFilterCriteria` builder 累積多個 criterion 再組 `WHERE`:

- 每個 criterion 帶 `Type`(`Date` / `Subquery` / 數值 / 文字…)、`Field`、`Operator`、`Value`、`Logic`(`AND` / `OR`)、`Description`。
- `AddCriterion`(多次)→ `BuildWhereClause()` 組出含括號的 `WHERE`;`Subquery` 類型用於把日期維度子查詢(`IN (SELECT [DateKey] FROM DATE_DIMENSION WHERE …)`)當成一個條件嵌入。
- 設計目標明確寫著:能一次組合**多條**(而非單條)條件的 AND/OR。

**對應現行**:進階篩選的 **AST 式 Query Builder**(`docs/action-contract-manifest.md` 的 Filter/Criteria AST schema、`docs/jet-frontend-description.md` 的 Step 4 Query Builder)即此設計意圖的演進與正式化。

---

## 6. 遷移動機（摘要）

取代 IDEA(訂閱授權成本與專有依賴)、降低對 SQL 與外部 IT 環境的依賴,目標**簡化 / 自動化 / 優化**審計流程,以涵蓋約八成案件的百萬筆級資料量為設計基準;十億筆級或雲端情境保留 SQL Server 縫隙。完整的棄用理由(IDEA 授權、Access 2GB 上限、VBA 無 AI/測試生態、打包與資安)見 `legacy/README.md`「為什麼棄用」,此處不重複。
