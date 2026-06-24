# Step 4 進階篩選 視覺精修與提示／組合器調整（接續 Approach B）

> **狀態：設計提案，待使用者核可後實作、再 GUI 驗收。** 純前端（`filter-step.js` / `ui-core.js` / `app.css`），不動後端、不動 `filter.preview` / `filter.commit` 契約；wire 形狀仍 `{name, rationale, groups}`，rule 不含 `__kctLetter`。

## 背景

接續 `2026-06-23-filter-query-builder-ux-redesign-design.md`（Approach B：每群組單一組合器）。Round 2 實作後，使用者提供實機截圖，確認仍有視覺與提示問題。本輪以**截圖實況**＋**網路研究**（NN/g、GOV.UK、Airtable、react-querybuilder、Cloudscape、Polaris、CSS-Tricks、Modern CSS）為據修正，避免臆測。兩個與先前指示衝突的抉擇已於 2026-06-24 對話定案：

- **欄位範例提示** → 常駐 helper text（覆蓋原 req 3 的 hover-only；依 NN/g、GOV.UK，hover-only/`title` 對鍵盤、觸控、報讀器與可發現性不友善，且欄位有預設值時 placeholder 不可用）。
- **KCT 群組預設組合器** → 全部（AND）（收掉舊「KCT 預設 OR」特例）。

## 對截圖實況的診斷

**已確認的問題**

1. **KCT 卡片高度參差**：同列以 grid `align stretch` 等高，但列與列落差大——停用卡（雙行標籤＋註記）最高、單行卡（如「空白摘要」）最矮，短卡下方留大片空白，整塊顯凌亂。即 req 2 的「卡片長度不一」。
2. **停用卡 A–D 佔據最顯眼頂部且最高**，整塊 KCT 乍看「壞掉一半」。
3. **動機 `textarea` 僅 2 列**，KCT 自動帶入 3 行被截斷需捲動。
4. **整頁偏長**：兩選取區塊＋彙總＋預覽＋已存＋矩陣堆疊。

**已確認正常／已不存在**：選取只改 `border-color`、無介面移位；情境名稱「F+G+J」與動機帶各條說明正確。
**次要**：字母框＋獨立 ✓ 兩個小標記略冗餘。

## 不變的資料模型

`draft = { name, rationale, groups: [ { join, rules: [ rule... ] } ] }`（同前一份 spec）。本輪不改契約：組合器仍＝群組內各規則 `join` 的一致值；後端逐條 join 評估等價。

## 變更項目

### A. 卡片統一與精簡（req 2）

> 依 CSS-Tricks「等高用 grid stretch + intrinsic、勿用固定像素高」、Modern CSS「選取態勿用會改盒寬的實線邊」。

- **統一卡片度量**：KCT 與自訂卡共用一致的 `min-height`，使單行卡與雙行卡等高；`label` 以 2 行 `line-clamp` 為上限，完整文字置於 `title`（hover 補全）。
- **KCT 卡移除冗餘 mark 欄**：選取態改由**字母框承載**（選取＝字母框轉實心反白／加勾，外加卡片淡藍底＋邊）。選取/未選不改盒寬（沿用 `border-color` 不變寬，必要處以透明邊框佔位 / `box-shadow`）。
- **停用卡（A/B/C/D）與啟用卡同高**、維持較輕的虛線＋降透明；註記縮小置於保留行，不再撐高卡片。
- **四個自訂分類**沿用同一條 grid 節奏與同款卡片，確保跨分類對齊一致。

### B. 欄位提示改常駐 helper text（抉擇 1）

> 依 NN/g「Placeholders…Harmful」「Tooltip Guidelines」、GOV.UK Text/Date input。

- 移除 `trailingDigits`（H）等欄位的 hover-only `title` 範例提示。
- 在該欄位控制項下方輸出**常駐 helper**：「例：999999 或 000000」。一行、簡短、不含句點（採 GOV.UK 風格，全專案一致）。
- 既有前導 `field-label`（如「金額尾數為」）保留；helper 只補範例值，不重述標籤。

### C. KCT 組合器預設改 AND（抉擇 2）

- **單規則 KCT**（A/C/D/E/F/G/H/J）累積進同一個 **AND 主群組**（新建群組預設 AND，收掉舊「KCT 預設 OR」特例，與自訂卡一致）。
- **非營業日(I)＝週末 OR 假日**，仍**自成一個 OR 群組**（本質語意，非預設值問題）。
- single-KCT 的落點為「最後一個非預設群組」；若最後一組是 I 的 OR 群組，**另開新 AND 群組**，避免把單條件併入 I 群組。
- **自訂卡**點擊：加到最後一群組，新群組預設 AND（不變）。

### D. 兩層邏輯視覺區隔（研究）

> 依 react-querybuilder / DbVisualizer / LogRocket：群組「間」連接器與群組「內」組合器須一眼可分，否則兩層邏輯被混淆。

- **群組內組合器**：維持標頭分段「符合〔全部│任一〕以下條件」。
- **群組間連接器**（`gi>=1`）：改為群組之間軌道上的小 **AND/OR 連接 pill**，與標頭分段在樣式上明顯不同。
- **群組卡左側細軌**（accent rail，1–2px、淺色）標示「包含關係」，符合 minimalist-ui（無漸層／重陰影／大圓角／emoji）。

### E. 動機欄與整頁瘦身

- **動機 `textarea`** 由 2 列改為可容 3–4 行（增 `rows` 或 `min-height`），避免 KCT 帶入被截斷。
- 兩個選取區塊收斂垂直 `padding` / `margin`、卡片 `min-height` 取緊湊值，降低整頁高度（保留「選取 vs 調整」的分段 macro whitespace）。

## 不變

- 純前端；wire 形狀 `{name, rationale, groups}`；`toWireScenario` 仍剝除 `__kctLetter`。
- KCT 多選命名（字母串＋各條說明）。名稱欄位本即可手動覆寫（自動帶入後仍可編輯）；本輪不另做命名 UI。
- 不新增／改名 action、不改 manifest。

## 受影響檔案

| 檔案 | 動作 |
|:--|:--|
| `wwwroot/js/steps/filter-step.js` | 範例提示由 `title` 改常駐 helper；KCT 卡移 mark 欄、字母框承載選取；`addKctToDraft` 改 AND 預設＋落點規則；`scenarioBuilderHtml` 連接器改 pill、群組加左軌；動機 `textarea` 加高 |
| `wwwroot/js/ui-core.js` | 視需要加 helper 文案常數／卡片短標；**無契約變更** |
| `wwwroot/css/app.css` | 卡片 `min-height`/`line-clamp`/字母框選取態／停用卡同高；helper text 樣式；連接器 pill 與群組左軌；`textarea` `min-height`；區塊 `padding` 收斂 |

## 驗收

- 純前端，無 `.cs`；`dotnet build/test` 不受影響。
- GUI 目視（Windows，列入 `docs/windows-handoff.md`）：KCT/自訂卡等高對齊、停用卡不再撐高；選取無位移；helper 範例常駐顯示；組合器預設 AND、I 仍 OR；連接器與組合器一眼可分；動機不截斷；整頁高度下降。
- wire 形狀不變，rule 不含 `__kctLetter`（`toWireScenario` 剝除）。
