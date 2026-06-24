# Step 4 彙總區扁平化（符合 全部／任一 條件）＋ 自訂區可折疊

> **狀態：設計提案，待使用者核可後實作、再 GUI 驗收。** 純前端（`filter-step.js` / `ui-core.js` / `app.css`），不動後端、不動 `filter.preview` / `filter.commit` 契約；wire 形狀仍 `{name, rationale, groups}`。承 r4，回應使用者實測回饋。使用者已選 **Approach A（扁平化）**（2026-06-24 對話）。

## 背景與問題

使用者實測 KCT 多選 **G+I+J**，看到**兩個群組＋藍色連接器 pill**，且不解「為何 E+H 只有一組、G+I+J 卻有兩組」。診斷：

- 單一條件（E/H/G/J…）累積進**同一個 AND 群組**；
- **非營業日(I)＝「週末 OR 假日」**無法塞進 AND 群組（一個群組只有一種組合器），**被迫自成一個 OR 群組** → 冒出第二群組＋連接器，且攤成「週末過帳／假日過帳」兩列；
- 兩層 AST 結構漏到 UI；群組/連接器是查詢建構器的專家概念，初學者門檻高。

## 設計：預設扁平、進階才分組

### 概念模型（使用者面）

- 情境呈現為「**符合〔全部(AND)│任一(OR)〕下列條件**」＋一條**扁平條件清單**。
- 清單項目兩類：
  - **簡單條件**（主群組的 rule）：型別下拉＋控制項＋移除（同現況 `ruleRowHtml`）。
  - **原子預設**（如 I）：**唯讀單行**「非營業日（週末或假日）」＋移除；不再攤成兩列、不顯示為「群組」。
- **頂層單一切換**「全部/任一」同時設定：主群組內各 rule 的 `join`、以及所有群組間連接器 `group.join`（保持一致）。預設 **全部(AND)**（承 r4）。
- 原子預設群組（I）**內部固定 OR**（語意不變），UI 只呈現一行。

### 何時扁平 / 何時進階

- 定義：「**可編輯群組**」＝非預設群組（無 `__kctPresetGroup`）；「**預設群組**」＝帶 `__kctPresetGroup`（如 I）。
- **可編輯群組 ≤ 1 → 扁平檢視**：無群組框、無連接器 pill，只有單一頂層「符合 全部/任一」切換＋扁平清單。涵蓋絕大多數 KCT／簡單自訂情境。
- **可編輯群組 ≥ 2 → 進階（分組）檢視**：沿用 r4 的群組卡＋標頭組合器＋連接器 pill；預設群組仍以**原子單行**呈現（不攤開）。
- 扁平檢視提供「**進階：分組**」鈕 → 新增一個空的可編輯群組（使可編輯群組＝2）切到進階檢視；移除群組回到 ≤1 即自動回扁平。

### AST 映射（不改契約）

- 底層仍是 `draft.groups`（兩層 AST）。
- **頂層切換**（扁平檢視）設值 X 時：對**每個 group** 設 `group.join = X`（連接器一致）；對**每個非預設 group** 另設其各 rule `join = X`（組合器一致）；**預設 group 的 rule `join` 不動**（保持 OR）。
- **加入簡單條件**（自訂卡 / 單規則 KCT）→ 併入主可編輯群組（無則新建），新 rule `join =` 頂層切換現值。
- **加入 I** → 自成 OR 預設群組（同 r4），其 `group.join =` 頂層切換現值。
- **移除原子預設行** → splice 該預設群組、`applyKctNaming`、`setFilterDraft`（KCT 卡因 `__kctLetter` 消失而同步取消）。
- **送後端**：`toWireScenario` 不變（仍 `{name, rationale, groups}`，剝 `__kctLetter`；group 層 `__kctPresetGroup` 因重建為 `{join, rules}` 自然不外洩）。**命中數與現況等價**——只有 UI 呈現變扁平，AST 與後端評估不變。

### 渲染細節

- 扁平清單順序：主群組 rule 依序 → 各預設原子行依 group 順序。
- 原子預設行標籤：依該預設對映固定字樣（I →「非營業日（週末或假日）」）；以 `FILTER_KCT_PRESETS`／`FILTER_KCT_CHECKLIST` 與一份「預設→原子標籤」對映取得。
- 頂層切換重用 `segmentedControl`（新 kind `scenario-combinator`），與 r4 的群組標頭組合器同款視覺但語意為「整個情境」。
- 進階檢視：可編輯群組用 r4 群組卡渲染；預設群組渲染為「一條原子行＋（gi>=1 時）連接器 pill」，不攤成 rule 列。
- 名稱／動機／必填／預覽／保存／已存清單／高風險矩陣：行為不變。

## 自訂篩選條件 可折疊

- 「自訂篩選條件」區塊改為**可折疊**，**預設收起**（避免長期佔版面）。
- 採本檔既有的 `hidden` 切換樣式（同 `toggle-matrix`／`toggle-scenario`）：標頭一顆鈕切換 body `hidden` 與 ▸/▾ 字樣，**不重建整個面板**，故不需動 Store／state。
- 收起時只剩標頭一列；展開顯示四分類卡片網格（同 r4 樣式）。**KCT 區塊維持常顯**（主入口，不折疊）。

## 受影響檔案

| 檔案 | 動作 |
|:--|:--|
| `wwwroot/js/steps/filter-step.js` | `scenarioBuilderHtml` 重構（扁平/進階分流、原子預設行、頂層切換）；新增 `remove-preset-group`、`scenario-combinator`、`toggle-custom-picker` 綁定；`customPickerHtml` 包成可折疊（標頭鈕＋hidden body） |
| `wwwroot/js/ui-core.js` | 新增「預設 key → 原子行標籤」對映（如 `kctNonBusinessDay`→「非營業日（週末或假日）」）；**無契約變更** |
| `wwwroot/css/app.css` | 扁平清單列樣式、原子預設行、可折疊區塊標頭；進階檢視沿用 r4 既有樣式 |

## 不變 / 邊界

- 純前端；wire `{name, rationale, groups}`；不改 manifest、不新增/改名 action；不動後端評估。
- minimalist-ui（1px hairline、crisp radius、無漸層/重陰影/大圓角主容器/emoji；spot pastel 點綴）；中文；ES5（`var`/function/IIFE/字串拼接/`Ui.esc`）。
- 不把業務邏輯移入前端。

## 驗收

- **G+I+J**：扁平三行（空白摘要／非營業日（週末或假日）／編製＝核准），一個「符合 全部/任一」切換，**無群組框/連接器 pill**；I **不再攤成兩列**。
- **E+H**：同樣扁平兩行——與 G+I+J **一致**，消除「有時分組有時不分組」的落差。
- 切「任一」→ 命中數變化合理；`toWireScenario` 仍乾淨（無 UI-only 鍵）。
- 點「進階：分組」→ 出現第二可編輯群組與連接器（進階檢視）；移回 ≤1 群組自動回扁平。
- **自訂篩選條件預設收起**、可展開；展開後加入條件正常。
- `dotnet build` 綠（純前端，後端護欄不受影響）；GUI 目視列入 `docs/windows-handoff.md`。
