# Step 4 進階篩選 非作用中組鎖定＋命名重算修復（r11，決策表自審）

> **狀態：自我審查＋修復提案，使用者已核可方向（AskUserQuestion：不新增前端測試層，改「決策表自審＋逐條紙上驗證」）。** 純前端（`filter-step.js` / `app.css`），不動後端、不動 wire 契約。

## 背景與測試現實

使用者反覆在第 1/2 組切換並取消 KCT 後，發現「邏輯順序錯亂」。本環境**無 JS runtime**（`node`/`npm` 不存在），且 `jet-testing` 規範**刻意不設前端測試層**（測試金字塔 .NET-only、禁止前端業務規則測試）。故採**決策表（decision table）＋狀態轉換（state transition）自審**（jet-testing §4 技術），逐列實作並紙上驗證；`dotnet build` 為回歸護欄（未動 `.cs`），GUI 由使用者驗收。

## 自我審查：三個疊加缺陷（錯亂真因）

1. **非作用中組未鎖定**：`[data-active-target]` handler 為避免奪焦而**排除 input/select/button**（filter-step.js），故點「非作用中組」的控制項**不會設它為作用中**，但 KCT 面板與新增落點反映的是**作用中組** → 改 A 組、面板卻是 B 組 → 錯亂。
2. **以 row〔移除〕刪 KCT 條件時名稱不重算**：remove-rule 只 `splice`＋`setFilterDraft`，**未 `applyKctNaming`**；type-change 同；**remove-set 亦同**（移除含 KCT 的組）。名稱與結構不一致。
3. **預設(I) 在名稱被當成獨立尾段** `G｜G+J｜I`：I 原子渲染在第 1 組 well（read-back 也 AND 進第 1 組），名稱卻擺尾段，看似三組。應併入第 1 組 token → `G+I｜G+J`。

## 修法

- **鎖定非作用中組**（缺陷 1）：`setWellHtml` 對 `multi && group && !isActive` 加 `scenario-set--locked`。CSS：條件清單與組合器 `opacity` dim ＋ `pointer-events:none`（內容不可互動，點任一處都落到 well → 設為作用中）；**保留**〔移除這組〕與「點此設為作用中」可點。只有作用中組可編輯。（鍵盤 tab-in 屬已知次要限制，本工具以滑鼠操作為主。）
- **命名重算守恆**（缺陷 2）：新增 `groupHasKct(group)`。remove-rule／type-change：**被移除/被改的 rule 帶 `__kctLetter` 才** `applyKctNaming`；remove-set：**被移除的組含 KCT 才**重算。守則：**只有涉及 KCT 字母的變動才重算名稱**——避免在純自訂情境誤清手改名稱（純自訂無 KCT，名稱本就手動）。
- **預設字母併入第一組 token**（缺陷 3）：`kctScenarioName` 多組分支把預設(I) 字母併入第一個 token、依 checklist 重排去重。

## 決策表（修正後的期望；逐列為紙上驗證骨架）

狀態：各組 KCT 字母／自訂、作用中組、預設。`active=gN` 表示作用中。命名為「組 token 以 ｜ 分隔」。

| # | 起始狀態 | 動作 | 期望條件結果 | 期望高亮（作用中視角） | 期望名稱 |
|--:|:--|:--|:--|:--|:--|
| 1 | g1{G,H} active | 點高亮 G | g1{H} | g1：H 亮、G 滅 | `H` |
| 2 | g1{G,H}、g2{} active | 點 G（標也在其他組） | g2{G} | g2：G 亮 | `G+H｜G` |
| 3 | g1{G,H}、g2{G,J} active=g2 | 點高亮 G | g2{J}，**g1 G 不動** | g2：J 亮、G 滅 | `G+H｜J` |
| 4 | g1{G,H}、g2{G,J} active=g2 | row〔移除〕G | g2{J}，**名稱即時更新** | g2：J 亮 | `G+H｜J` |
| 5 | g1{G,H}、g2{G,J} active=g1 | 點 J（標也在其他組） | g1{G,H,J} | g1：G,H,J 亮 | `G+H+J｜G+J` |
| 6 | g1{G,H}、g2{G,J} active=g2 | 改 g2 的 G 型別為自訂 | g2{自訂,J}，名稱更新 | g2：J 亮、G 滅 | `G+H｜J` |
| 7 | g1{G}、g2{J} active=g2 | 點 g1 鎖定區任一處 | active=g1（**不改 g1 任何值**） | g1：G 亮、J 標也在其他組 | `G｜J`（不變） |
| 8 | g1{G,H} active | 〔＋另一組條件〕 | g2{}、active=g2 | g2 空；G,H 標也在其他組 | `G+H`（g2 空略過） |
| 9 | g1{G}、g2{J} active=g2 | 〔移除這組〕g2 | g1{G}、active=g1 | g1：G 亮 | `G`（**重算**，G+...→G） |
| 10 | g1{G}、preset I、g2{G,J} | — | — | — | `G+I｜G+J`（**併入第一組**） |
| 11 | g1{自訂}（無KCT，手改名"X"）| row〔移除〕自訂 | g1{} | — | **`X` 不變**（守恆，不誤清） |
| 12 | 任一狀態 | 送出（preview/save） | — | — | wire 無 `__active`/`__kctLetter`/`__kctPresetGroup` |

## 受影響檔案

| 檔案 | 動作 |
|:--|:--|
| `wwwroot/js/steps/filter-step.js` | `setWellHtml` 加 `scenario-set--locked`；新增 `groupHasKct`；remove-rule／type-change／remove-set 依「涉及 KCT 字母」守恆呼叫 `applyKctNaming`；`kctScenarioName` 多組分支併入預設字母到第一組 token |
| `wwwroot/css/app.css` | `.scenario-set--locked`（條件清單與組合器 dim＋`pointer-events:none`） |

## 驗收

- 逐列紙上推演決策表 1–12 皆符；`dotnet build` 0 警告 0 錯誤；grep 類名兩側成對。
- GUI 目視（列入 `docs/windows-handoff.md`）：非作用中組變淡且點不動其控制項、點它即設為作用中；row 移除/改型別/移除這組後名稱即時正確；`G+I｜G+J` 命名；純自訂手改名不被清。
