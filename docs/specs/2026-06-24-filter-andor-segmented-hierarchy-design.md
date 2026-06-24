# Step 4 進階篩選 AND/OR 段控與父子層級對比（r8）

> **狀態：設計提案，使用者已核可（AskUserQuestion 兩題皆採推薦案）。** 純前端（`filter-step.js` / `app.css`），不動後端、不動 wire 契約（仍兩層 `groups` AST）。承 r7 的統一「條件組」介面，回應使用者：兩層組合器改 **AND/OR 段控**、做出**父子層級視覺對比**、read-back 寫進 **AND/OR 布林邏輯**。

## 背景

r7 把分組融成一套乾淨介面（每組一塊 well、`＋另一組條件` 無模式、教學空狀態、read-back）。使用者實機後回饋三點：

1. 組間連接器整句 radio「符合任一組即可／需符合所有組」**太吃閱讀成本**、不直覺 → 改成直接選 **AND / OR**。
2. 組內「這一組要怎麼搭配？＋radio」同樣改 **AND / OR**，且要與組間運算子做出**父子層級對比**，讓使用者一眼分辨「設定條件群組」與「設定篩選情境」的差別。
3. 下方藍色 read-back 很好（可作底稿的條件邏輯），但要把 **AND/OR 邏輯寫進去**。

## 研究收斂（皆有來源）

- **AND/OR 用 segmented control（小段控）優於 radio**：2–4 個對立、互斥選項時，段控可見度高、一眼看到全部與當前選中，比 radio／下拉更快更不歧義（UX Movement、Component Gallery、Cieden）。
- **用顏色區分 AND vs OR 降低認知負荷**；「群組 vs 單一條件」要靠**縮排＋顏色＋視覺區隔**做出父子層級（Garofalo UX、LogRocket 視覺層級）。
- **react-querybuilder** 巢狀層級用 **branch 線（軌道）＋縮排＋顏色** 表示並封頂深度——印證「軌道＋上色＋縮排」是父子結構的標準語彙。
- 要附查詢的**白話／布林預覽**幫助理解——read-back 把 AND/OR 寫進去更精準。

## 設計

### 1. 兩層組合器都改 AND/OR 段控（`comboSegment`）

- 兩格 `AND` / `OR`，**mono 字型**（minimalist-ui 把運算子/meta 歸 mono），目前值高亮。
- 純 `AND`／`OR`、不加冗詞（依使用者「太吃閱讀成本」回饋）；滑鼠移上 `title` tooltip 給白話（AND→每個條件都成立／OR→符合任一個就好），完整白話交給 read-back。
- 底層仍是 `<input type=radio>`，沿用既有 `data-set-combinator`／`data-set-join`／`data-gi`／`data-sync-presets` 屬性與 change 綁定，**綁定邏輯幾乎不動**，只換視覺外殼。值仍為 `AND`/`OR`。

### 2. 父子層級對比（一套色彩語意貫穿）

- **組內運算子（子／群組層）**：`combo-seg--group`，**灰階、較小**，靠在「第 N 組」標題旁、**在組框內**（縮排、中性色）。單組時放在 well 頂端、前綴白話 lead「條件之間」。
- **組間運算子（父／情境層）**：`combo-seg--scenario`，**藍色、較大**，置中**跨在一條水平軌道（`set-rail`）上**，前綴小 lead「組間」。和「第 N 組」標題形成明顯對比。
- **藍色＝情境層**這個語意同時對應下方藍色 read-back，使用者會學起來「藍的＝最上層邏輯」。
- 漸進揭露：組合器只在該組 ≥2 條件時顯示（沿用 r7）。

### 3. read-back 改行內布林式（鏡像控制項）

- 句首白話 lead：「這個情境會找出符合下列邏輯的分錄：」。
- 後接布林式：
  - 單組：`條件A AND 條件B`（1 條件時只列該條件、無運算子）。
  - 多組：`（G1 內部式） OR （G2 內部式）`，每組內部以該組運算子相連；單條件組省略括號。
- **運算子上色＝控制項同一套**：OR（情境層）藍色粗體、AND（組內）灰色 mono。read-back 成為 UI 結構的鏡像，也直接可當底稿的條件邏輯。

### 4. AST 映射與防呆（沿用 r7，不變）

- 可編輯群組→well；預設群組（I）→原子行併入第一塊 well；第一塊 well 段控帶 `data-sync-presets`（連動預設群組 join）。
- 組間段控（`data-set-join`）把所有非預設群組 join 設一致值（單一組間運算子）。
- `toWireScenario` 仍過濾空群組；空 set 不報錯；移除 set 到剩一組自動回乾淨單 well。

### 5. 不變

- 純前端；wire `{name, rationale, groups}`；命中數與改版前等價（只是呈現改變）。
- 教學空狀態、4 拍提示、KCT 多選命名、非營業日原子行不變。
- minimalist-ui（淡底 well、hairline、無重框/陰影/emoji）；中文；ES5。

## 受影響檔案

| 檔案 | 動作 |
|:--|:--|
| `wwwroot/js/steps/filter-step.js` | 新增 `comboSegment`（段控）；`setWellHtml` 改用段控（多組放 head、單組放 `combo-row`＋lead）；`interSetConnectorHtml` 改 `set-rail`＋藍色段控；`readBackHtml` 改行內布林式（`expr-op` 上色）；移除 `setComboRadio`；移除變死碼的 `scenarioTopCombinator`；綁定沿用（`data-set-combinator`/`data-set-join` 不變） |
| `wwwroot/css/app.css` | 新增 `.combo-seg*`、`.combo-row*`、`.set-rail*`、`.expr-op*`；移除死碼 `.scenario-radio*`、`.set-connector*`、`.scenario-flat__q`、`.scenario-flat__chooser` |

## 驗收

- **單組（G+H）**：well 頂端「條件之間 [AND|OR]」灰階小段控；下方 read-back「…下列邏輯的分錄：摘要空白 AND 金額尾數 000000」。
- **G+I+J**：仍一塊 well（非營業日原子行併入），段控同步預設群組。
- **＋另一組條件**：第 1、2 組各自頭部一個灰階小段控；兩組之間一條軌道、藍色較大段控「組間 [AND|OR]」，與「第 N 組」對比明顯。
- **read-back 多組**：「（… AND …） OR （… AND …）」，OR 藍粗、AND 灰，隨段控即時更新。
- 切段控後〔預覽這個情境〕命中數隨布林邏輯改變；`toWireScenario` 仍乾淨。
- `dotnet build` 綠；死碼 grep 歸零；GUI 目視列入 `docs/windows-handoff.md`。
