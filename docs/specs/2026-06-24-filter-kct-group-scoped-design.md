# Step 4 進階篩選 KCT 對齊「作用中組」＋組感知命名（r10）

> **狀態：設計提案，使用者已核可（AskUserQuestion：KCT 跟隨作用中組 toggle；命名以組分隔 `G+H｜J`）。** 純前端（`filter-step.js` / `app.css`），不動後端、不動 wire 契約（仍兩層 `groups` AST）。

## 衝突診斷（使用者實測 r9 後回報）

KCT 是**情境層級**設計，但 r9 後條件變成**組層級**，層級不一致＝衝突：

| 機制 | 現層級 | 行為 |
|:--|:--|:--|
| `isKctSelected` 已選判定 | 情境層（掃全部組） | 任一組有 G → G 卡亮 |
| `removeKctFromDraft` 取消 | 情境層（掃全部組） | 點已選 G → **刪所有組的 G** |
| `kctScenarioName` 命名 | 情境層（全部字母） | `G+H+J` 不分組 |
| 新增（r9） | 組層（作用中組） | 只進作用中組 |

實測：作用中＝第 2 組時點 G，因第 1 組已有 G、`isKctSelected` 判「已選」→ 走取消 → **誤刪第 1 組的 G**；名稱 `G+H+J` 看不出組別。使用者結論正確：KCT 這些機制是「整個情境只有一組」時代的設計。

## 設計（KCT 對齊作用中組，UX 優先）

把 KCT 從情境層對齊到組層，與 r9 作用中模型一致。

### 1. KCT 卡「已選/取消」跟隨作用中組（單規則）

- **`isKctSelectedActive(draft, item)`**（卡片高亮）：單規則 KCT 看**作用中組**是否含該字母；預設(I) 是情境層獨立 OR 群組、仍看全情境。
- **取消**：單規則只從作用中組移除（`removeKctFromActiveGroup`，同訊號在別組不動，且不丟棄變空的作用中組）；預設(I) 整組移除（`removeKctFromDraft`）。
- **新增**：沿用 r9 `addKctToDraft`（單規則→作用中組；I→自成一組）。
- 切換作用中組 → 面板（每次 `setFilterDraft` 全重繪）跟著反映那組的 KCT。
- **「也在其他組」次級標記**（`isKctUsedElsewhere`）：某單規則 KCT 用於別的非作用中組、但作用中組沒有時，卡片加藍邊＋小字「也在其他組」（藍邊無底 vs 已選的藍邊＋淡藍底），維持全局意識。
- 「已選 N 項」計數＝作用中組的 KCT 數；面板 intro 改寫點明「條件加到作用中組、亮起＝已在作用中組」。

### 2. 命名以「組」區分（`kctScenarioName` 重寫）

- **單一可編輯組**：沿用「全部所選字母排一排」（`G+H`、`G+I+J`），最乾淨。
- **多可編輯組**：每組字母 `+` 串、**組間 `｜` 分隔**；只有自訂條件的組顯示「自訂」；預設(I) 字母附最後一個 token。例：第 1 組 {G,H}、第 2 組 {J} → `G+H｜J`；同訊號跨組 → `G+H｜G+J`。
- 動機（`kctScenarioRationale`）維持情境層（列出所有所選字母的說明，去重），不分組——它是說明非結構宣告。
- 名稱仍可手改；`applyKctNaming` 在每次 KCT toggle 重算（既有行為）。

### 3. 不變

- 純前端；wire `{name, rationale, groups}`；`__kctLetter`/`__kctPresetGroup`/`__active` 不外洩。
- 非營業日(I) 仍是情境層獨立群組（特例，因其結構上就是一個 OR 群組）。
- read-back（布林邏輯）已是全局精確真相，命名只是短標籤。

## 受影響檔案

| 檔案 | 動作 |
|:--|:--|
| `wwwroot/js/steps/filter-step.js` | 新增 `isPresetNewGroup`／`isKctSelectedActive`／`isKctUsedElsewhere`／`removeKctFromActiveGroup`／`kctLettersInGroup`；重寫 `kctScenarioName` 組感知；KCT 點擊 handler 改用 `isKctSelectedActive`＋分流移除；`kctPickerHtml` 卡片高亮改 active 判定、加「也在其他組」標記、intro 改寫 |
| `wwwroot/css/app.css` | `.picker-card--elsewhere`（藍邊無底）、`.picker-card__elsewhere`（小字標記） |

## 驗收

- **單組 G+H**：行為不變；名稱 `G+H`；卡片亮 = 在該組。
- **多組**：選 G+H（第 1 組），＋另一組（第 2 組作用中），上方 G/H 卡顯示「也在其他組」（藍邊無底、不亮）、J 可加且亮。
- **點第 2 組的 G**：**加到第 2 組、第 1 組 G 不動**（不再誤刪）；名稱變 `G+H｜G+J`。
- **切回第 1 組**：G/H 卡亮、J 顯示「也在其他組」。
- 名稱多組以 `｜` 分組；只有自訂的組顯「自訂」；單組仍 `G+H`。`toWireScenario` 仍乾淨。
- `dotnet build` 綠；GUI 目視列入 `docs/windows-handoff.md`。
