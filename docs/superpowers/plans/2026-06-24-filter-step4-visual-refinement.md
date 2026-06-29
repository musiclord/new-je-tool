# Step 4 進階篩選 視覺精修 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: 以 superpowers:subagent-driven-development（逐 task 派工＋兩段複審）或 superpowers:executing-plans（本 session 內逐 task 執行）實作。各 task 為**循序**（共用 `filter-step.js` / `app.css`，不可平行）。步驟以 checkbox（`- [ ]`）追蹤。

**Goal:** 修掉 Step 4 截圖實況的視覺問題（卡片高度參差、停用卡撐高、提示方式、兩層邏輯難分、動機截斷、整頁偏長），並落實兩個已定抉擇（提示改常駐 helper text、KCT 組合器預設改 AND）。

**Architecture:** 純前端三檔（`filter-step.js` markup/行為、`ui-core.js` 資料常數、`app.css` 樣式）＋一處前端 JS 預設值（`addKctToDraft`）。不動後端、不動 wire 契約。依 `docs/specs/2026-06-24-filter-step4-visual-refinement-design.md`。

**Tech Stack:** Vanilla ES5（`var`/function、IIFE、字串拼接 HTML、`Ui.esc` 轉義）、CSS（minimalist-ui token）、WebView2 執行期載入。

**驗證策略（重要，偏離標準 TDD 的理由）:** 本批為純前端 markup/CSS＋一處前端 JS 預設值。專案**無 JS 單元測試 runner**（node 未安裝），且 jet-testing 硬邊界**禁止** WinForms/WebView2 E2E。故各 task 驗證＝(1) **靜態自審**：逐行讀改動區、`Grep` 確認類名一致與無孤兒引用、括號/引號配對；(2) **`dotnet build`**：確認未誤動 `.cs`（本批不應動任何 `.cs`，build 須維持綠）；(3) **人工 GUI 目視**：寫入 `docs/windows-handoff.md` 待 Windows 端驗收。每個 task 的「驗證」步驟即上述靜態檢查，不編造假單元測試。

## Global Constraints

> 每個 task 的需求都隱含包含本節（逐條自 spec／AGENTS.md 原樣帶）。

- 純前端（`wwwroot/js/steps/filter-step.js`、`wwwroot/js/ui-core.js`、`wwwroot/css/app.css`）；**不動後端、不動 `filter.preview` / `filter.commit` 契約**。
- wire 形狀恆為 `{ name, rationale, groups }`；`groups` 內每條 rule **不含 `__kctLetter` 等 UI-only 鍵**（`toWireScenario` 深拷貝剝除；group 層級的 UI-only 鍵亦因 `toWireScenario` 重建 group 為 `{join, rules}` 而自然丟棄）。
- **不新增／改名 action、不改 `docs/action-contract-manifest.md`**；保留既有 `data-bind` 識別碼語意。
- 遵 minimalist-ui：`1px` 實線 hairline、crisp radius（`--radius-sm`）、**無漸層／重陰影（opacity ≤ 0.05）／大圓角主容器／emoji**；spot pastel（`--pale-blue` 等）僅點綴。小型 tag/pill（連接器）允許 `border-radius: 999px`。
- **不把業務邏輯移入** `Form1.cs`/HTML/CSS/JS；前端只組裝 AST 與渲染，規則一律後端參數化 SQL 評估。
- 不在 `jet-api.js` 以外呼叫 `window.chrome.webview.postMessage`。
- UI 文案一律**中文**。
- ES5 風格與既有檔一致（`var`/function、IIFE、字串拼接、`Ui.esc`）；不引入 `let`/箭頭函式/模板字串。

---

## File Structure

| 檔案 | 責任 | 本批改動 |
|:--|:--|:--|
| `wwwroot/js/steps/filter-step.js` | Step 4 markup 與行為 | 卡片 markup（去 KCT mark 欄、label 加 title）；trailingDigits helper；`addKctToDraft` AND 預設；連接器 pill＋綁定；動機 `textarea` 加高 |
| `wwwroot/js/ui-core.js` | 篩選資料常數 | 本批**無契約變更**；不需改動（保留以備微調註解） |
| `wwwroot/css/app.css` | 樣式 | 卡片 `min-height`/`line-clamp`/字母框選取態/停用卡同高；`.rule-field` helper；連接器 pill＋群組左軌；`textarea`；區塊 padding 收斂 |

各 task 循序套用於「前一 task 完成後的現況檔」。Task 1 動 picker 區 CSS＋picker markup；Task 2 動 rule-row helper；Task 3 純 JS 邏輯；Task 4 動 builder 區 CSS＋builder markup＋綁定。CSS 交疊區段不同（picker ≈ 1530–1745、builder ≈ 1750–1900），循序執行無衝突。

---

## Task 1: Picker 卡片系統（統一高度、字母框承載選取、停用卡同高、區塊瘦身）

**Files:**
- Modify: `wwwroot/js/steps/filter-step.js`（`kctPickerHtml` 卡片內層、`customPickerHtml` 卡片內層）
- Modify: `wwwroot/css/app.css`（`.condition-picker*`、`.picker-card*` 區段，約 1530–1745）

**Interfaces:**
- Consumes: `Ui.FILTER_KCT_CHECKLIST`（letter/label/disabled/note）、`Ui.FILTER_RULE_GROUPS`、`availableRuleTypes()`、既有 class（`.picker-card`、`.picker-card--kct/--custom/--selected/--disabled`、`.picker-card__letter/__label/__note/__mark`）。
- Produces: KCT 卡 DOM 不再含 `.picker-card__mark`（僅自訂卡保留 `＋` mark）；`.picker-card__label` 帶 `title=完整文字`；CSS：`.picker-card { min-height }`、`.picker-card__label` 2 行 `line-clamp`、`.picker-card--kct` 兩欄 grid、`.picker-card--selected .picker-card__letter` 反白、`.picker-card--disabled .picker-card__label` 1 行 clamp。後續 task 不依賴本 task 的視覺值。

- [ ] **Step 1：改 `kctPickerHtml` 卡片內層（移除 mark 欄、label 加 title）**

把 `kctPickerHtml` 內回傳卡片的片段（現含 `picker-card__mark`）改為：

```js
      return '<button type="button" class="' + cls + '"' +
          ' data-kct-letter="' + item.letter + '"' +
          ' aria-pressed="' + (selected ? 'true' : 'false') + '"' +
          (disabled ? ' disabled aria-disabled="true"' : '') + '>' +
        '<span class="picker-card__letter" aria-hidden="true">' + Ui.esc(item.letter) + '</span>' +
        '<span class="picker-card__label" title="' + Ui.esc(item.label) + '">' + Ui.esc(item.label) + '</span>' +
        (note ? '<span class="picker-card__note">' + Ui.esc(note) + '</span>' : '') +
      '</button>';
```

（即刪掉 `'<span class="picker-card__mark" ...>' + (selected ? '✓' : '') + '</span>' +` 一行，並在 `picker-card__label` 加 `title`。選取態改由字母框承載，見 CSS。）

- [ ] **Step 2：改 `customPickerHtml` 卡片 label 加 title（保留 `＋` mark）**

把 `customPickerHtml` 內卡片片段改為：

```js
        return '<button type="button" class="picker-card picker-card--custom"' +
            ' data-action="add-rule" data-rule-type="' + t.value + '">' +
          '<span class="picker-card__mark" aria-hidden="true">＋</span>' +
          '<span class="picker-card__label" title="' + Ui.esc(t.quickLabel || t.label) + '">' + Ui.esc(t.quickLabel || t.label) + '</span>' +
        '</button>';
```

- [ ] **Step 3：CSS — 卡片統一高度與 label clamp（`.picker-card`、`.picker-card__label`）**

`.picker-card` 加 `min-height` 並略收 padding；`.picker-card__label` 加 2 行 `line-clamp`：

```css
.picker-card {
  display: grid;
  align-content: start;
  gap: 4px 10px;
  min-height: 60px;
  padding: 10px 14px;
  text-align: left;
  font-family: var(--font-sans);
  color: var(--color-text);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  cursor: pointer;
  transition: border-color 200ms var(--ease-soft),
    background-color 200ms var(--ease-soft),
    box-shadow 200ms var(--ease-soft),
    transform 120ms var(--ease-soft);
}

.picker-card__label {
  font-size: 13px;
  line-height: 1.45;
  color: var(--color-text);
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  overflow: hidden;
}
```

- [ ] **Step 4：CSS — KCT 卡兩欄 grid＋字母框承載選取態**

把 `.picker-card--kct` 由三欄改兩欄、`label`/`note` 移到第 2 欄；把舊的 `.picker-card--selected .picker-card__mark` 規則改成作用於字母框；停用卡 label clamp 為 1 行：

```css
.picker-card--kct {
  grid-template-columns: 26px 1fr;
}

.picker-card--kct .picker-card__label {
  grid-column: 2;
}

.picker-card__note {
  grid-column: 2;
  font-family: var(--font-mono);
  font-size: 10px;
  letter-spacing: 0.04em;
  color: var(--color-muted);
}

/* 已選態：卡片淡藍底＋邊（保留），並把字母框反白為實心 ink，作為「在情境裡」的強訊號（取代舊的獨立 ✓ 欄）。 */
.picker-card--selected {
  background: var(--pale-blue);
  border-color: var(--pale-blue-ink);
}

.picker-card--selected .picker-card__letter {
  color: #fff;
  background: var(--color-ink);
}

/* 停用卡：label 收為 1 行，使「1 行 label＋註記」總高與啟用卡的 2 行 label 等高，整列不被撐高。 */
.picker-card--disabled .picker-card__label {
  -webkit-line-clamp: 1;
}
```

（刪除舊的 `.picker-card--selected .picker-card__mark { color: var(--pale-blue-ink); }` 規則——KCT 已無 mark 欄，自訂卡永不 selected。）

- [ ] **Step 5：CSS — 區塊垂直瘦身**

收斂選取區塊與彙總區的垂直留白（降低整頁高度，保留分段感）：

```css
.condition-picker {
  margin-bottom: 14px;
  padding: 14px 18px;
  background: var(--color-surface-sunken);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
}

.condition-picker__intro {
  margin: 0 0 10px;
  font-size: 12px;
  line-height: 1.6;
  color: var(--color-muted);
}

.condition-picker__group {
  margin-bottom: 12px;
}

.scenario-builder {
  margin-top: 22px;
}
```

- [ ] **Step 6：靜態驗證**

- `Grep` `picker-card__mark` 於 `filter-step.js`：應**只剩自訂卡**一處（`customPickerHtml`）。
- `Grep` `picker-card__mark` 於 `app.css`：`.picker-card__mark` 基底規則仍在（自訂卡用），但**無** `.picker-card--selected .picker-card__mark`。
- `Grep` `grid-column: 3` 於 `app.css` 的 picker 區：應為 0（label/note 已移到第 2 欄）。
- 逐行讀 `kctPickerHtml`／`customPickerHtml`：字串拼接的 `+` 與引號配對正確、`title` 值經 `Ui.esc`。
- 確認未動任何 `.cs`。

---

## Task 2: 範例提示改常駐 helper text（trailingDigits / H）

**Files:**
- Modify: `wwwroot/js/steps/filter-step.js`（`ruleControlsHtml` 的 `case 'trailingDigits'`）
- Modify: `wwwroot/css/app.css`（新增 `.rule-field` / `.rule-field__hint`；調整 `.rule-row__controls input[type="text"]` 選擇器為直接子代）

**Interfaces:**
- Consumes: 既有 `.rule-row__controls` flex 版面、`.rule-row__field-label`、`data-rule-bind="keywords"`。
- Produces: `.rule-field`（直欄容器：input 上、hint 下）與 `.rule-field__hint`（常駐範例文字）；`.rule-row__controls > input[type="text"]`（直接子代選擇器，避免巢狀 input 被套上欄寬 flex-basis）。後續 task 不依賴。

- [ ] **Step 1：改 `trailingDigits` case（去 hover title、加常駐 helper）**

```js
      case 'trailingDigits':
        // 範例提示改常駐 helper text（NN/g、GOV.UK：hover-only/title 對鍵盤/觸控/報讀器不友善；
        // 欄位有預設值 000000，placeholder 不可用，故範例放欄位下方常駐一行）。
        return '<span class="rule-row__field-label" title="顯示金額整數尾數符合任一樣態（捨小數）">金額尾數為</span>' +
          '<span class="rule-field">' +
            '<input type="text" data-rule-bind="keywords" value="' + Ui.esc(rule.keywords) + '">' +
            '<span class="rule-field__hint">例：999999 或 000000</span>' +
          '</span>';
```

- [ ] **Step 2：CSS — `.rule-field` 直欄容器＋ hint，並修正 input 選擇器**

把既有：

```css
.rule-row__controls input[type="text"] {
  flex: 1 1 180px;
  min-width: 140px;
}
```

改為**直接子代**（讓 `.rule-field` 內的巢狀 input 不被套上會變成「高度 180px」的 flex-basis），並新增容器與 hint：

```css
.rule-row__controls > input[type="text"] {
  flex: 1 1 180px;
  min-width: 140px;
}

/* 帶範例 helper 的欄位：input 在上、常駐範例在下；整體在 rule-row 內佔位同一條文字輸入。 */
.rule-field {
  display: inline-flex;
  flex-direction: column;
  gap: 2px;
  flex: 1 1 180px;
  min-width: 140px;
}

.rule-field input {
  width: 100%;
}

.rule-field__hint {
  font-size: 11px;
  line-height: 1.4;
  color: var(--color-muted);
}
```

- [ ] **Step 3：靜態驗證**

- `Grep` `title="例：` 於 `filter-step.js`：應為 0（hover 範例已移除）。
- `Grep` `rule-field__hint` 於 `filter-step.js` 與 `app.css`：各 1 處定義/使用。
- `Grep` `rule-row__controls input\[type="text"\]`（無 `>`）於 `app.css`：應為 0（已改直接子代）。
- 逐行讀 `case 'trailingDigits'`：引號/`+` 配對正確，`Ui.esc(rule.keywords)` 保留。

---

## Task 3: KCT 組合器預設改 AND（含 I 群組連接器與單規則落點）

**Files:**
- Modify: `wwwroot/js/steps/filter-step.js`（`addKctToDraft`）

**Interfaces:**
- Consumes: `kctRuleSpecs(item)`、`materializeRule(spec, letter)`、`groupCombinator(group)`、`Ui.FILTER_KCT_PRESETS`、`draft.groups`。
- Produces: 群組層級 UI-only 標記 `__kctPresetGroup`（標在 I 這類 `newGroup` 的群組上；`toWireScenario` 重建 group 為 `{join, rules}` 時自然丟棄，**絕不外洩 wire**）。`addKctToDraft` 行為：單規則 KCT 累積進「最後一個非預設群組」、組合器預設 **AND**；`newGroup`（I）自成群組、組內 **OR**、連接器 **AND**。`isKctSelected`/`removeKctFromDraft`/`toWireScenario` 不需改（皆作用於 rule 或重建 group）。

- [ ] **Step 1：改寫 `addKctToDraft`（AND 預設＋落點略過預設群組）**

```js
  function addKctToDraft(draft, item) {
    var specs = kctRuleSpecs(item);
    if (specs.length === 0) { return; }

    var preset = item.kind === 'preset'
      ? Ui.FILTER_KCT_PRESETS.filter(function (p) { return p.key === item.ref; })[0]
      : null;

    // 預設 I（非營業日 = weekend OR holiday）自成一組：組內固定「任一(OR)」（本質語意：命中任一非營業日即算）；
    // 與前一組的連接器預設「全部(AND)」（抉擇 2：多選 KCT 預設 AND）。群組打 __kctPresetGroup 標記，
    // 讓單規則 KCT 的落點略過它（避免把單條件併入 I 的 OR 群組而違反 AND 預設）。
    // __kctPresetGroup 是 UI-only：toWireScenario 重建 group 為 {join, rules}，此鍵自然不外洩。
    if (preset && preset.newGroup) {
      draft.groups.push({
        join: 'AND',
        __kctPresetGroup: true,
        rules: specs.map(function (s) {
          var r = materializeRule(s, item.letter);
          r.join = 'OR';
          return r;
        })
      });
      return;
    }

    // 單規則 KCT（A/C/D/E/F/G/H/J）：累積進「最後一個非預設群組」＝KCT 主群組；
    // 沒有就新建一組、組合器預設「全部(AND)」（抉擇 2，與自訂卡一致）。新規則 join 取該群組現有
    // 組合器以維持群組內一致（groupCombinator）。
    var target = null;
    for (var i = draft.groups.length - 1; i >= 0; i--) {
      if (!draft.groups[i].__kctPresetGroup) { target = draft.groups[i]; break; }
    }
    if (!target) {
      target = { join: 'AND', rules: [] };
      draft.groups.push(target);
    }
    var combinator = target.rules.length ? groupCombinator(target) : 'AND';
    specs.forEach(function (spec) {
      var r = materializeRule(spec, item.letter);
      r.join = combinator;
      target.rules.push(r);
    });
  }
```

（自訂卡的 add-rule 綁定維持不變——它已是新群組預設 AND、`rule.join = last.rules.length ? groupCombinator(last) : 'AND'`。）

- [ ] **Step 2：靜態驗證**

- `Grep` `__kctPresetGroup` 於 `filter-step.js`：定義/讀取共 2 處（push 標記、落點迴圈判斷）；確認**無**任何送出點直接引用它（送出一律經 `toWireScenario`）。
- 讀 `toWireScenario`：確認 group 仍重建為 `{ join: g.join, rules: ... }`，未複製其他 group 鍵（故 `__kctPresetGroup` 不外洩）。
- 推演 3 情境（紙上）：
  - 選 F→G→J：應落同一新 AND 群組（`join` 不影響首組；rule.join 全 AND）。
  - 選 I：自成群組 `__kctPresetGroup`、`join:'AND'`、rules `weekend/holiday` 皆 `join:'OR'`。
  - 選 I 後再選 J：J 落點略過 I 群組 → 新建 AND 群組；最終 groups = [I(OR 內、AND 連接), J(AND)]。
- 確認 `var i` 宣告於迴圈（ES5，無 `let`）。

---

## Task 4: 彙總區兩層邏輯視覺區隔（連接器 pill＋群組左軌）＋動機 textarea 加高

**Files:**
- Modify: `wwwroot/js/steps/filter-step.js`（`scenarioBuilderHtml` 的 `connector` 與動機 `textarea`；`bind` 的 `[data-segmented]` 處理移除 group-join 分支、新增 `[data-group-join-toggle]` 處理）
- Modify: `wwwroot/css/app.css`（`.group-connector*`、`.scenario-group` 左軌；移除 `.group-connector__hint`）

**Interfaces:**
- Consumes: `segmentedControl('group-combinator', ...)`（標頭組合器**維持** segmented）、`draft.groups[gi].join`、`Store.setFilterDraft`、`Store.getState().filter.draft`。
- Produces: 群組間連接器改為單顆 pill 切換鈕（`.group-connector__toggle` + `data-group-join-toggle` + `data-gi`，左右各一條 `.group-connector__line`），點按在 AND↔OR 間翻轉 `group.join`；`.scenario-group` 加 `border-left` accent rail。標頭組合器與 `data-segmented="group-combinator"` 行為不變。

- [ ] **Step 1：改 `scenarioBuilderHtml` 的 `connector`（segmented → pill 切換）**

把現有 `var connector = ...`（含 `segmentedControl('group-join', ...)` 與 `.group-connector__hint`）改為：

```js
          // 群組間連接器（gi>=1）：與標頭「組合器(segmented)」刻意不同款——軌道上一顆 pill 切換鈕，
          // 一眼分辨「群組間 AND/OR」與「群組內 全部/任一」兩層邏輯（研究：react-querybuilder/DbVisualizer）。
          var connector = gi === 0 ? '' :
            '<div class="group-connector">' +
              '<span class="group-connector__line" aria-hidden="true"></span>' +
              '<button type="button" class="group-connector__toggle" data-group-join-toggle data-gi="' + gi + '"' +
                ' aria-label="切換與上一組的關係（AND／OR）">與上一組：' +
                (group.join === 'OR' ? 'OR（任一群組成立）' : 'AND（兩組同時成立）') +
              '</button>' +
              '<span class="group-connector__line" aria-hidden="true"></span>' +
            '</div>';
```

（標頭 `combinator`＝`segmentedControl('group-combinator', ...)` 與其前後綴「符合／以下條件」**不動**。）

- [ ] **Step 2：動機 `textarea` 由 2 列改 4 列**

在 `scenarioBuilderHtml` 找到動機欄，`rows="2"` 改 `rows="4"`：

```js
        '<label class="form__row">' +
          '<span class="form__label">篩選動機說明 <em class="form__req">*</em></span>' +
          '<textarea class="form__input" rows="4" data-bind="scenario-rationale" placeholder="說明這個情境為何值得保留到工作底稿">' +
            Ui.esc(draft.rationale) + '</textarea>' +
        '</label>' +
```

- [ ] **Step 3：`bind` — 移除 group-join segmented 分支、新增連接器 toggle 綁定**

把 `[data-segmented]` 處理器內的 `group-join` 分支移除（連接器已不用 segmented），只留 `group-combinator`：

```js
    container.querySelectorAll('[data-segmented]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var kind = btn.getAttribute('data-segmented');
        var gi = Number(btn.getAttribute('data-index'));
        var value = btn.getAttribute('data-value');
        var draft = Store.getState().filter.draft;
        var group = draft.groups[gi];
        if (!group) { return; }
        if (kind === 'group-combinator') {
          group.rules.forEach(function (r) { r.join = value; });
        }
        Store.setFilterDraft(draft);
      });
    });

    // 群組間連接器（pill）：翻轉此群組與前一組的 join（AND↔OR）。
    container.querySelectorAll('[data-group-join-toggle]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var gi = Number(btn.getAttribute('data-gi'));
        var draft = Store.getState().filter.draft;
        var group = draft.groups[gi];
        if (!group) { return; }
        group.join = group.join === 'OR' ? 'AND' : 'OR';
        Store.setFilterDraft(draft);
      });
    });
```

- [ ] **Step 4：CSS — 連接器 pill＋軌道線、群組左軌、移除舊 hint**

```css
/* 群組間連接器：軌道線＋置中 pill 切換鈕。與標頭 segmented（實心 ink）明顯不同款（pill、淡藍），
   讓「群組間 AND/OR」與「群組內 全部/任一」兩層邏輯一眼可分。 */
.group-connector {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 6px 0 12px;
}

.group-connector__line {
  flex: 1;
  height: 1px;
  background: var(--color-border);
}

.group-connector__toggle {
  padding: 3px 12px;
  font-family: var(--font-mono);
  font-size: 12px;
  color: var(--pale-blue-ink);
  background: var(--pale-blue);
  border: 1px solid var(--pale-blue-ink);
  border-radius: 999px;
  cursor: pointer;
  white-space: nowrap;
  transition: background-color 160ms var(--ease-soft);
}

.group-connector__toggle:hover {
  background: #d4ebfb;
}
```

把 `.scenario-group` 加左側 accent rail（標示「包含關係」）：

```css
.scenario-group {
  margin-bottom: 12px;
  padding: 12px 14px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-left: 3px solid var(--pale-blue-ink);
  border-radius: var(--radius-sm);
}
```

刪除舊的 `.group-connector__hint { ... }` 規則（已無此元素）。

- [ ] **Step 5：靜態驗證**

- `Grep` `group-join'`（segmented 舊值）於 `filter-step.js`：應為 0（連接器已不用 `segmentedControl('group-join', ...)`，`[data-segmented]` 也無 group-join 分支）。
- `Grep` `group-connector__hint` 於 `filter-step.js` 與 `app.css`：應各為 0（HTML 與 CSS 都已移除）。
- `Grep` `data-group-join-toggle` 於 `filter-step.js`：markup 1 處＋綁定 1 處。
- `Grep` `segmentedControl(` 於 `filter-step.js`：應只剩 `group-combinator` 一處呼叫。
- 逐行讀新 `connector` 與兩個綁定：引號/`+` 配對、`Number(...)` 取值、`Store.setFilterDraft(draft)` 收尾。

---

## Task 5: 收尾驗證與交接

**Files:**
- Modify: `docs/windows-handoff.md`（新增本輪 GUI 目視驗收卡）

- [ ] **Step 1：`dotnet build`（回歸護欄）**

Run: `dotnet build src/JET/JET.slnx --no-restore --nologo`
Expected: 綠（本批未動任何 `.cs`，build 不應受影響；若紅，代表誤動或既有問題，須查明）。

- [ ] **Step 2：全域類名孤兒掃描**

- `Grep` 下列類名於 `filter-step.js` 與 `app.css`，確認 markup 與 CSS 兩側一致、無單側孤兒：`picker-card__mark`、`picker-card__letter`、`rule-field`、`rule-field__hint`、`group-connector__line`、`group-connector__toggle`、`group-connector__hint`（後者應 0）。

- [ ] **Step 3：寫入 windows-handoff GUI 驗收卡**

於 `docs/windows-handoff.md` 新增一張卡，列出待 Windows 端目視項目：
- KCT／自訂卡**等高對齊**；停用卡（A–D）不再撐高頂部。
- 選取 KCT 卡：字母框反白＋卡片淡藍，**無介面移位**。
- H 條件列：欄位下方**常駐**「例：999999 或 000000」，非 hover。
- 多選 KCT（如 F+G+J）：彙總群組組合器預設 **全部(AND)**；單獨選 I 仍為 OR 群組；I 後再選 J 不併入 I 群組。
- 兩群組以上：群組間 **pill 連接器**與標頭 **segmented 組合器**一眼可分；群組卡左側細軌可見。
- 動機 `textarea` 可顯示 KCT 帶入的多行不截斷。
- 整頁高度較前一版下降；預覽／保存／矩陣行為不變。

---

## Self-Review

**1. Spec coverage（spec 各節 → task）**
- A 卡片統一與精簡 → Task 1（去 mark 欄、字母框選取、min-height、label clamp、停用卡同高）＋ Task 1 Step 5（區塊瘦身）。✓
- B 提示改常駐 helper → Task 2。✓
- C 組合器預設 AND（含 I 連接器、單規則落點）→ Task 3。✓
- D 兩層邏輯視覺區隔（連接器 pill、群組左軌）→ Task 4 Step 1/4。✓
- E 動機 textarea＋整頁瘦身 → Task 4 Step 2（textarea）＋ Task 1 Step 5（瘦身）。✓
- 驗收（純前端、build 綁、GUI 目視、wire 不變）→ Task 5。✓

**2. Placeholder scan:** 無 TBD/TODO/「適當處理」類字樣；每個改動皆附完整 old→new 代碼與確切類名/值。✓

**3. Type/識別子一致性:**
- 新類名跨檔一致：`.rule-field` / `.rule-field__hint`（Task 2 JS 用、CSS 定義）；`.group-connector__line` / `.group-connector__toggle`（Task 4 JS 用、CSS 定義）。✓
- `data-group-join-toggle` / `data-gi`：Task 4 markup 與綁定同名。✓
- `__kctPresetGroup`：Task 3 push 與落點迴圈同名；不被 `toWireScenario` 複製。✓
- 標頭組合器仍 `data-segmented="group-combinator"` / `data-index`；連接器改 `data-gi`（兩者不混用）。✓
- 移除項：`.picker-card--selected .picker-card__mark`（CSS）、`group-join` segmented（JS markup＋分支）、`.group-connector__hint`（JS＋CSS）、`title="例：...`（JS）——各於對應 task 的驗證步驟以 Grep 確認歸零。✓

---

## Execution Handoff

計畫已存於 `docs/superpowers/plans/2026-06-24-filter-step4-visual-refinement.md`。

**建議執行方式：單一連貫實作 ＋ 逐 task 自審（inline / executing-plans）。**
理由：四個 task 高度耦合、共改 `filter-step.js` 與 `app.css`，且本質是一個**連貫的視覺系統**；上一輪的失敗正是把這種連貫改動拆給彼此隔離的 subagent。循序的 subagent-driven 也可行（各 subagent 看到的是前一 task 合併後的現況），但對這種小而密的視覺收斂，單一實作者一次到位、逐 task 對照 spec 自審，較能保證 token 與視覺一致性。

兩種選項（由使用者定）：
1. **Inline（建議）** — 本 session 內逐 task 套用、每 task 跑靜態驗證，全部完成後一次 `dotnet build`＋寫 handoff。
2. **Subagent-Driven（循序）** — 每 task 派全新 subagent（四件套：task 全文＋Global Constraints 原樣＋scene 2–3 句＋Taste primer），task 間兩段複審；**循序非平行**（共用檔）。
