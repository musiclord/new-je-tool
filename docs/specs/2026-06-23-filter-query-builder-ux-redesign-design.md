# 進階篩選查詢建構器 UX 重整（Approach B：分組組合器）

> **狀態：設計已核准（2026-06-23 對話，使用者選定 Approach B），待實作後 GUI 驗收。** 純前端（`filter-step.js` / `ui-core.js` / `app.css`），不動後端、不動 `filter.preview` / `filter.commit` 契約。

## 背景

上一輪 Step 4「進階條件篩選」前端重設計出現品質問題，使用者要求修正並重新研究查詢建構器 UX：

1. **介面移位**：彙總卡裡「建議名稱」chip 是條件式才出現的列，出現時把下方欄位往下推、且寬度隨內容變動。
2. **自訂卡 `×N` 徽章**：上一輪擅自加的同型別計數，語意不明。
3. **KCT 命名**：多選 KCT 條件時，名稱應直接是字母串（如 `E+F+G`），動機是各條 KCT 條件的詳細說明。
4. **查詢建構器現代化**：把現代 query builder（每群組單一組合器）套用到審計篩選的兩層 AST。

## 不變的資料模型（兩層 AST）

`draft = { name, rationale, groups: [ { join, rules: [ rule... ] } ] }`

- `group.join`（`gi>=1`）：此群組與**前一群組**的連接（AND/OR）；第一組無連接。
- `rule.join`：群組**內**規則間的連接。本設計約束：同一群組內所有規則的 `join` 一致 = 該群組的「組合器」。如此把舊版「逐條 AND/OR」收斂成「每群組一個組合器」，**不需改契約**——後端仍收逐條 join，全 AND＝符合全部、全 OR＝符合任一，評估等價。

## 一、必修四項

1. **移除建議名稱 chip 與藍框**：刪 `suggestScenarioName`、chip HTML、`apply-name-suggestion` 綁定，以及 CSS `.scenario-suggest*`。移位來源隨之消失。
2. **移除自訂卡 `×N` 徽章**：`customPickerHtml` 不再依草稿計數；自訂卡僅以靜態「＋」當加入示意，不隨點擊變動文案。
3. **KCT 多選命名**：
   - `kctScenarioName(draft)`：取草稿中帶 `__kctLetter` 的字母，依 A→J 排序、以 `+` 串接（`E+F+G`）。
   - `kctScenarioRationale(draft)`：對每個所選字母，逐行列出其 `FILTER_KCT_CHECKLIST.label`（字母＋說明）。
   - KCT 勾選/取消後即重算並寫回 `name` / `rationale`；無 KCT 規則時兩欄清空（交回手動）。
4. **一律中文** UI 文案與回覆。

## 二、Approach B：分組組合器（查詢建構器）

每個群組卡：

- **群組標頭**：`群組 N` ＋ 一個分段組合器「符合〔全部│任一〕以下條件」（`data-group-combinator`）＋〔移除群組〕。組合器恆顯示於標頭（位置穩定、不造成移位）。
- **群組間連接**：`gi>=1` 時，在群組卡之間放一顆分段連接器〔AND│OR〕（`data-group-join`）。
- **條件列**：移除逐條 AND/OR 下拉（`ruleRowHtml` 不再渲染 joinCell）；只留〔型別下拉〕〔該型別控制項〕〔移除〕。
- 底部：〔新增條件群組〕〔預覽測試母體〕〔預覽這個情境〕〔保存為篩選情境〕。

### 組合器與連接器行為

- `groupCombinator(group)`：群組內規則 join 一致值；無規則時預設依「群組建立來源」（見下）。
- 切換組合器 → 把該群組**所有規則**的 `join` 設為該值（保持一致），`setFilterDraft`。
- 切換群組間連接器 → 設 `group.join`。
- 加入條件到某群組時，新規則的 `join` 取該群組目前組合器值（維持一致）。

### 加入落點與預設組合器

- 自訂卡點擊：加到**最後一個群組**；若無群組，新建一組、組合器預設 **全部(AND)**（一般篩選慣例）。
- KCT 單規則（A/C/D/E/F/G/H/J）：加到最後一個群組；若需新建，組合器預設 **任一(OR)**（多紅旗命中任一即算，避免 AND 零命中陷阱）。
- KCT 預設 I（非營業日）：`newGroup`[weekend OR holiday] 自成一組，組合器 **任一(OR)**。
- 移除 KCT 規則後變空的群組一併移除。

## 三、受影響檔案

| 檔案 | 動作 |
|:--|:--|
| `wwwroot/js/steps/filter-step.js` | 移除 chip/×N；加 `kctScenarioName`/`kctScenarioRationale` 並於 toggle 套用；`addKctToDraft` 設組合器預設；`scenarioBuilderHtml` 改 Approach B 群組渲染；`ruleRowHtml` 去 joinCell；新增組合器/連接器綁定；自訂加入設 join＝組合器 |
| `wwwroot/js/ui-core.js` | 無契約變更；必要時微調註解 |
| `wwwroot/css/app.css` | 移除 `.scenario-suggest*`；新增分段組合器 `.scenario-group__combinator`、群組間連接器 `.group-connector` 樣式（沿用 minimalist-ui 既有 token，無漸層/重陰影/pill 大圓角/emoji） |

## 四、驗收

- 純前端，無 `.cs` 變更；`dotnet build/test` 不受影響。
- GUI 目視驗收（Windows）：無介面移位、組合器/連接器運作、KCT 命名正確、自訂卡無 ×N、預覽/保存正常。列入 `docs/windows-handoff.md`。
- wire 形狀仍 `{name, rationale, groups}`，rule 不含 `__kctLetter`（`toWireScenario` 剝除）。
