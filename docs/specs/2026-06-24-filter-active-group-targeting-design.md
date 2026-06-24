# Step 4 進階篩選 作用中（active）群組目標模型（r9）

> **狀態：設計提案，使用者已核可（模型由使用者指定；作用中視覺 AskUserQuestion 採「左側藍軸＋淡藍底＋徽章」）。** 純前端（`filter-step.js` / `app.css`），不動後端、不動 wire 契約（仍兩層 `groups` AST）。

## 背景與決策

承 r8。使用者實測後提三點：

1. **「特定金額尾數」灰字提示把輸入框頂歪**（介面錯位）。
2. **「自訂篩選條件」面板太佔空間**，要整體縮小、簡潔。
3. **分出第 2 組後，無法回頭把條件加到第 1 組**——KCT 卡與自訂卡都只加到「最後一組」。

對 #3 我先提「逐組『＋ 加條件』」（Airtable 慣例），但使用者指出這會與上方兩塊面板**定位衝突**（「若逐組能加條件，那上方面板存在的意義為何？」），並**指定改用「作用中（active）群組」模型**：保留上方面板為唯一新增入口，使用者**點選某一組設為作用中**，上方面板新增的條件就進到那一組；作用中的組要用明顯前端風格做對比。**取消逐組『＋ 加條件』。**

研究背景（前輪已收斂並引用）：active/selected panel 是「選取狀態」模式，需用邊框＋上色＋標籤明確區分（NN/g 選取狀態不可只靠單一線索）；query builder 的「新增落點」要明確（Garofalo UX）。

## 設計

### 1. 作用中群組模型（取代「永遠加到最後一組」）

- **`__active`**：UI-only 標記，標示「上方面板新增的條件要進哪一組」。恆有**至多一個非預設群組**帶 `__active`。`toWireScenario` 把 group 重建為 `{join, rules}`，此鍵自然不外洩（同 `__kctPresetGroup`）。
- **`activeEditableGroup(draft)`**：回傳帶 `__active` 的非預設群組；無則回退**最後一個**非預設群組；再無則 null。
- **`setActiveGroup(draft, group)`**：清掉所有 `__active`，把指定（非預設）群組設為 `__active`。
- **加入落點改用作用中組**：`addKctToDraft`（單規則 KCT）與自訂 `add-rule` 綁定，目標由「最後一個非預設群組」改為 `activeEditableGroup(draft)`；無則新建一組並設為作用中。預設群組（I）仍自成一組、不受影響。
- **新增第 2 組（＋另一組條件）**：push 新群組後 `setActiveGroup` 設為作用中（新組即新落點）。
- **移除某組（移除這組）**：splice 後 `setActiveGroup(draft, activeEditableGroup(draft))` 重新標記（移除作用中組則回退到最後一組）。
- **點組設為作用中**：點該組 well 的「中性區域」（非表單控制項、非按鈕）即設為作用中——排除 `button/input/select/textarea/label`（避免攔截編輯、避免重繪奪焦）。

### 2. 作用中視覺對比（使用者選案：左側藍軸＋淡藍底＋徽章）

- **作用中組**：`scenario-set--active`——**左側 3px 藍軸（`::before`，pale-blue-ink）＋淡藍底（pale-blue）＋ pale-blue-ink 邊**；組標頭一枚**「作用中」徽章**（filled pale-blue-ink pill）。
- **其他組**：一般 hairline、無底色；`cursor: pointer`、hover 邊框轉強；組標頭一行**常駐**小灰字「點此設為作用中」（可點＝該標頭/中性區可設作用中）。
- **單組時不顯示作用中樣式**（無對比對象，保持乾淨）；**≥2 組**才出現作用中／提示。
- **全域提示**（≥2 組時，情境區）：「點任一組設為『作用中』，上方面板新增的條件會加到該組。」

### 3. 附帶處理（使用者原 #1、#2）

- **#1 提示錯位修復**：成因＝`金額尾數為` 那欄的 input＋hint 是上下堆疊的一欄，放進垂直置中的 `.rule-row` 被整欄置中，input 被頂到上半、與同列其他控制項錯位。修法＝`.rule-field__hint` 改 `position: absolute`（掛在 input 正下方、不再撐高該欄），`.rule-row:has(.rule-field__hint)` 預留列底空間 → input 回到與同列控制項置中對齊、hint 乾淨落下且不壓到下一列。（WebView2 Chromium 支援 `:has()`。）
- **#2 自訂面板精簡**：面板**保留**（它是新增入口），但 `.condition-picker--custom` 範圍內：卡片變矮（取消 60px 最小高、改單行 label）、欄寬收窄（180→150px）、群距與內距收斂、移除展開後重複的說明句。KCT 面板不動。

### 4. 不變

- 純前端；wire `{name, rationale, groups}`；命中數與改版前等價。
- 兩層 AST、`toWireScenario` 過濾空組、KCT 命名/標記/移除、非營業日原子行、AND/OR 段控與布林 read-back（r8）皆不變。
- minimalist-ui（淡底、hairline、pale 點綴、無重陰影/emoji）；中文；ES5。

## 受影響檔案

| 檔案 | 動作 |
|:--|:--|
| `wwwroot/js/steps/filter-step.js` | 新增 `activeEditableGroup`／`setActiveGroup`；`addKctToDraft` 與自訂 `add-rule` 改打作用中組；`add-set`／`remove-set` 維護 active；新增「點 well 設作用中」綁定；`setsHtml`／`setWellHtml` 加 active 樣式類、徽章/提示、`data-active-target`；`scenarioBuilderHtml` 加全域提示；精簡自訂面板說明句 |
| `wwwroot/css/app.css` | `#1` `.rule-field`/`.rule-field__hint`（絕對定位）＋`.rule-row:has(.rule-field__hint)`；`#2` `.condition-picker--custom` 精簡覆寫；`active` 群組樣式（`.scenario-set--active` 軸/底/邊、`.scenario-set__badge`、`.scenario-set__hint`、inactive hover、`.scenario-active-hint`） |

## 驗收

- **#1**：「特定金額尾數」列的 input 與型別下拉、〔移除〕同列置中對齊；「例：999999 或 000000」乾淨落在 input 下方、不壓上方輸入框、不壓下一列。
- **#2**：「自訂篩選條件」展開後明顯小一圈（卡片矮、單行、欄較密、無冗句）；KCT 面板不變。
- **#3 / active**：單組時無作用中樣式；**＋另一組條件** → 新組自動成「作用中」（左藍軸＋淡藍底＋「作用中」徽章），第 1 組轉為一般樣式並顯示「點此設為作用中」。
- **點第 1 組** → 第 1 組變作用中、第 2 組轉一般；此時點上方 KCT 卡或自訂卡，新條件**加到第 1 組**。點輸入框/按鈕**不會**誤切作用中、不奪焦。
- 全域提示在 ≥2 組時出現。`toWireScenario` 仍乾淨（無 `__active`/`__kctPresetGroup`/`__kctLetter`）。
- `dotnet build` 綠；GUI 目視列入 `docs/windows-handoff.md`。
