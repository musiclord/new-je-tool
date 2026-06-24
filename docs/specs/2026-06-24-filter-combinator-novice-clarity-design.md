# Step 4 扁平檢視組合器：白話化＋消除孤兒感（非專業者一眼就懂）

> **狀態：設計提案，待使用者核可後實作、再 GUI 驗收。** 純前端（`filter-step.js` / `app.css`），不動後端、不動 wire 契約。承 r5，回應使用者實測回饋。使用者已選 **Approach 2（提問＋整句 radio）**（2026-06-24 對話，看過 mock）。

## 背景與問題

r5 扁平檢視把組合器做成「符合〔全部│任一〕下列條件」的 segmented 切換，夾在動機欄與條件清單之間。使用者實測回報：它像「**沒人照顧的孤兒**」——非專業者第一眼看不出「這是做什麼」「怎麼做」，違反易用性。

三路網路研究結論（皆有來源）：
- **孤兒感＝間距 bug**（控制項上下間距相等→哪邊都不屬於）。修法：把控制項＋條件清單**框進同一個區域（common region）**、控制項當這區的標題、下方貼近清單（NN/g common-region、visual-hierarchy、form white-space；GOV.UK fieldset/legend；8yd 標題間距；react-querybuilder 群組結構）。
- **裸「全部/任一」是最大混淆點**；**整句 radio＋點出後果**對新手最不歧義（NN/g checkboxes-vs-radio、MS Windows radio 指南；Apple Mail 整句；Baymard 結果數）。
- **只有一條條件時隱藏組合器**，加到第二條才出現（Airtable、NN/g 漸進揭露）。
- **即時命中數**雖佳，但需後端夠快；本工具預覽是 SQL 查詢（母體可達百萬列），**不採每次切換自動查詢**，改用常駐「後果文字」。

## 設計（僅改扁平檢視的組合器呈現；AST 與後端評估不變）

### 1. 同框消除孤兒（common region）
- 扁平檢視的「組合器＋條件清單」放進**一個淡色 well**（`--color-surface-sunken` 底＋圓角，**不另加邊框**以免與外層 rule-card 形成巢狀框，符合「勿過度加框」）。
- 組合器是這個 well 的**標題**，下方一條 hairline 分隔線後緊接條件清單（下方間距小、上方靠 well 內距，控制項「擁有」清單）。

### 2. 提問＋整句 radio＋後果（白話、不歧義）
- well 頂部一句提問：**「這些條件要怎麼搭配？」**
- 兩個 radio（互斥，`name="scenario-combinator"`）：
  - **全部都要符合** — 每個條件都成立（結果較少）
  - **符合任一即可** — 符合一個就好（結果較多）
- 目前值由 `scenarioTopCombinator(draft)` 決定（AND→全部、OR→任一）；切換＝沿用既有語意：把所有 `group.join` 與所有「非預設」群組的 rule `join` 設為該值（預設群組 I 內部 OR 不動）。不再用 segmented、不用 AND/OR 術語、不用 on/off 開關、不用下拉。

### 3. 漸進揭露（只在 ≥2 條件時顯示）
- 扁平條件數 `flatCount = Σ(預設群組記 1；否則該群組 rules 數)`。
- `flatCount < 2` → **不顯示組合器**（一條條件無從「搭配」，顯示無意義）；well 內只有清單。
- `flatCount ≥ 2` → 顯示提問＋兩 radio。

### 4. 進階檢視不變
- 進階（分組）檢視（可編輯群組 ≥2）維持 r4/r5 的群組卡＋標頭 `group-combinator` segmented＋連接器 pill。本輪只改**扁平預設檢視**的組合器。

## 受影響檔案

| 檔案 | 動作 |
|:--|:--|
| `wwwroot/js/steps/filter-step.js` | `flatScenarioHtml` 重寫（well 包裹＋提問＋整句 radio＋漸進揭露；移除 segmented `scenario-combinator`）；新增 `radioOption` helper；`bind` 移除 `[data-segmented]` 的 `scenario-combinator` 分支、新增 `[data-scenario-combinator]` radio change 綁定 |
| `wwwroot/css/app.css` | `.scenario-flat` 改淡色 well；新增 `.scenario-flat__chooser`/`__q`/`.scenario-radio`/`__label`/`__effect`；移除 r5 的 `.scenario-flat__head`/`__label` |

## 不變 / 邊界

- 純前端；wire `{name, rationale, groups}`；組合器切換語意與 r5 相同（只是控制項型別改 radio）；命中數與改版前等價。
- 不改 manifest、不新增 action；不動後端評估、不自動查詢。
- minimalist-ui（hairline、淡彩 well、無重框/漸層/emoji）；中文；ES5。

## 驗收

- 兩條以上條件（如 G+H）：彙總區條件區是一個**淡色 well**，頂部「**這些條件要怎麼搭配？**」＋兩個整句 radio（全部都要符合／符合任一即可，各帶後果），well 內接條件清單——控制項明顯是清單的標題、**不再像孤兒**。
- **只剩一條條件**時，組合器**不顯示**（well 內只有那一條）。
- 切 radio → 既有 `〔預覽這個情境〕` 命中數隨布林邏輯改變；`toWireScenario` 仍乾淨。
- 進階檢視（按「進階：分組」）不受影響。
- `dotnet build` 綠；GUI 目視列入 `docs/windows-handoff.md`。
