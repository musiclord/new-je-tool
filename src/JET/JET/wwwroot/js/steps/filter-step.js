/*
  Step 4：進階條件篩選（條件 AST + Query Builder）。
  前端只組裝 AST 與渲染；條件由後端轉參數化 SQL 評估，前端不計算規則。

  版面（由上而下）：
    1. 「KCT條件」選取區塊  —— A–J 可複選 toggle，累積成同一情境的條件。
    2. 「自訂篩選條件」選取區塊 —— 四組等寬卡片，點一張＝新增一條可重複的自訂條件。
    3. 「建立篩選情境」彙總調整區塊 —— 彙總全部已選條件，設定數值/下拉/AND-OR，命名與動機必填。
    4. 預覽結果／已儲存情境／高風險條件矩陣（行為不變）。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  // 鏡像閘門：科目配對／授權清單未匯入時不提供需要它的型別/預篩選鍵（權威驗證在後端）。
  function availableRuleTypes() {
    var imp = Store.getState().importState;
    return Ui.FILTER_RULE_TYPES.filter(function (t) {
      return (!t.requiresAccountMapping || !!imp.accountMapping) &&
        (!t.requiresAuthorizedPreparers || !!imp.authorizedPreparer);
    });
  }

  function availablePrescreenKeys() {
    var imp = Store.getState().importState;
    return Ui.PRESCREEN_KEY_OPTIONS.filter(function (o) {
      return (!o.requiresAccountMapping || !!imp.accountMapping) &&
        (!o.requiresAuthorizedPreparers || !!imp.authorizedPreparer);
    });
  }

  /* ============================================================================
     KCT 卡 ↔ 草稿成員：介面小、實作深的少數函式（Ousterhout）。
     一張 KCT 卡 = 一份「rule 規格」陣列（kctRuleSpecs）。加入 = 把這些規格化成真 rule、
     在每條 rule 上打一個穩定的身分標記（__kctLetter = 卡片字母）併進草稿；已選偵測 =
     草稿中是否存在帶該卡字母標記的 rule；移除 = 把所有帶該卡字母標記的 rule 挑掉。

     為何用標記而非結構簽章（取捨顯式）：先前以 type(+field/mode/prescreenKey) 的結構簽章
     猜「這條 rule 是不是這張卡帶進來的」。但 KCT 卡的簽章會與查核員「自訂」的同型別條件
     重疊——例如卡 F(customKeywords) 會被任何手動加入的自訂關鍵字條件誤判為「已選」，且移除
     時只刪「第一條」同簽章 rule，可能誤刪使用者手動加入的條件。改用精確身分標記後，這一整類
     誤判／誤刪的 edge case 直接消失（Linus）：自訂的同型別條件不帶標記，永遠不會被當成 KCT。

     __kctLetter 是 UI-only 標記（鍵名以雙底線開頭、明示「內部用途、不屬 wire 契約」），絕不可
     送進後端。所有送出點一律經 toWireScenario() 深拷貝並遞迴剝除，wire 形狀恆為
     { name, rationale, groups }，groups 內每條 rule 不含任何 UI-only 鍵（見 toWireScenario）。
     ============================================================================ */

  // rule 身分標記鍵：標示「這條 rule 是哪張 KCT 卡帶進來的」。UI-only，絕不送 wire（由
  // toWireScenario 剝除）。集中為常數，讓打標記／偵測／剝除三處引用同一事實。
  var KCT_LETTER_KEY = '__kctLetter';

  // 一張 KCT 卡會建立的「rule 規格」陣列（純資料；type 必填，其餘為覆寫鍵）。
  //   kind:'type'   → 單一規格 { type: ref }，落地為 newFilterRule(ref)。
  //   kind:'preset' → 沿用 FILTER_KCT_PRESETS：newGroup 為多條規格，否則單一 overrides 規格。
  function kctRuleSpecs(item) {
    if (item.kind === 'preset') {
      var preset = Ui.FILTER_KCT_PRESETS.filter(function (p) { return p.key === item.ref; })[0];
      if (!preset) { return []; }
      return preset.newGroup ? preset.newGroup.slice() : [preset.overrides];
    }
    return item.ref ? [{ type: item.ref }] : [];
  }

  // 是否為「自成一組」的預設 KCT（目前只有 I＝非營業日：weekend OR holiday）。這類本質是情境層級的獨立
  // OR 群組，卡片 toggle 維持情境層級；其餘單規則 KCT 為組層級（見 isKctSelectedActive）。
  function isPresetNewGroup(item) {
    if (item.kind !== 'preset') { return false; }
    var preset = Ui.FILTER_KCT_PRESETS.filter(function (p) { return p.key === item.ref; })[0];
    return !!(preset && preset.newGroup);
  }

  // 把一條規格落地成真 rule，並打上該卡的身分標記：以 newFilterRule(type) 為底，套上規格的
  // 覆寫鍵（field/mode/prescreenKey/join…），最後標 __kctLetter = letter（UI-only，剝除後才送 wire）。
  function materializeRule(spec, letter) {
    var rule = Ui.newFilterRule(spec.type);
    Object.keys(spec).forEach(function (k) { rule[k] = spec[k]; });
    rule[KCT_LETTER_KEY] = letter;
    return rule;
  }

  // 已選偵測：草稿中是否存在帶該卡字母標記的 rule。純比對身分標記，不再做結構簽章猜測，
  // 故使用者自訂的同型別條件（不帶標記）永遠不會誤觸已選。空規格＝停用卡，不算已選。
  function isKctSelected(draft, item) {
    if (kctRuleSpecs(item).length === 0) { return false; }
    return draft.groups.some(function (g) {
      return g.rules.some(function (r) { return r[KCT_LETTER_KEY] === item.letter; });
    });
  }

  // 卡片「已選」判定（組層級）：單規則 KCT 看「作用中組」是否含該字母；預設(I) 是情境層級獨立群組，看全情境。
  // 讓同一訊號可在不同組各自存在，且在作用中組 toggle 只動該組（解 r9 後 KCT 情境層 toggle 的誤刪衝突）。
  function isKctSelectedActive(draft, item) {
    if (kctRuleSpecs(item).length === 0) { return false; }
    if (isPresetNewGroup(item)) { return isKctSelected(draft, item); }
    var active = activeEditableGroup(draft);
    return !!active && active.rules.some(function (r) { return r[KCT_LETTER_KEY] === item.letter; });
  }

  // 單規則 KCT 是否「用於其他（非作用中）可編輯組」——給卡片加淡標記「也在其他組」，維持全局意識。
  function isKctUsedElsewhere(draft, item) {
    if (isPresetNewGroup(item)) { return false; }
    var active = activeEditableGroup(draft);
    return draft.groups.some(function (g) {
      return !g.__kctPresetGroup && g !== active &&
        g.rules.some(function (r) { return r[KCT_LETTER_KEY] === item.letter; });
    });
  }

  // KCT 卡視覺狀態（給 kctPickerHtml）。把「情境層級的預設(I)」和「組層的單規則」分開，避免 I 用組層藍色
  // 高亮而被誤讀為「已加入作用中組」：
  //   'preset'    = 預設(I)，已加入情境（不論作用中組）——黃色「情境層級」標記。
  //   'selected'  = 單規則、在作用中組（藍色高亮）。
  //   'elsewhere' = 單規則、只在別的（非作用中）組（藍邊「也在其他組」）。
  //   'none'      = 未加入。
  function kctCardState(draft, item) {
    if (isPresetNewGroup(item)) { return isKctSelected(draft, item) ? 'preset' : 'none'; }
    if (isKctSelectedActive(draft, item)) { return 'selected'; }
    if (isKctUsedElsewhere(draft, item)) { return 'elsewhere'; }
    return 'none';
  }

  // 加入：把該卡的規格落地成帶標記的 rule 併進草稿（就地修改傳入 draft）。
  //   preset 的 newGroup（如 I：非營業日 weekend OR holiday）自成一組帶入（組內 OR、連接器 AND），打 __kctPresetGroup 標記；
  //   其餘單規則併入最後一個「非預設」群組（沒有就先開一個 AND 群組）——多卡累積成同一情境（預設 AND）。
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

    // 單規則 KCT（A/C/D/E/F/G/H/J）：累積進「作用中」群組（activeEditableGroup）；沒有可編輯組就新建
    // 一組、組合器預設「全部(AND)」並設為作用中。新規則 join 取該群組現有組合器以維持群組內一致。
    var target = activeEditableGroup(draft);
    if (!target) {
      target = { join: 'AND', rules: [] };
      draft.groups.push(target);
      setActiveGroup(draft, target);
    }
    var combinator = target.rules.length ? groupCombinator(target) : 'AND';
    specs.forEach(function (spec) {
      var r = materializeRule(spec, item.letter);
      r.join = combinator;
      target.rules.push(r);
    });
  }

  // 移除：把所有帶該卡字母標記的 rule 從草稿挑掉，並清掉因此變空的群組（就地修改傳入 draft）。
  // 只刪自身標記者——使用者手動加入的同型別條件（無標記）絕不被動到；preset I 的整組因兩條都帶
  // 同一字母標記而一起清空，該空 group 隨即被濾除，不留空 group、不破壞其餘 group 的 join 結構。
  function removeKctFromDraft(draft, item) {
    draft.groups.forEach(function (g) {
      g.rules = g.rules.filter(function (r) { return r[KCT_LETTER_KEY] !== item.letter; });
    });
    draft.groups = draft.groups.filter(function (g) { return g.rules.length > 0; });
  }

  // 取消單規則 KCT：只從「作用中組」移除該字母的 rule（同訊號在別組不動）；不丟棄變空的作用中組
  //（使用者正在編輯它，空組於 toWireScenario 送出時才略過）。
  function removeKctFromActiveGroup(draft, item) {
    var active = activeEditableGroup(draft);
    if (!active) { return; }
    active.rules = active.rules.filter(function (r) { return r[KCT_LETTER_KEY] !== item.letter; });
  }

  // wire 投影（單一收斂點）：把一個情境（草稿或已存）深拷貝成 filter.preview / filter.commit 的
  // wire 形狀——只 { name, rationale, groups }，且 groups 內每條 rule 都遞迴剝除 __kctLetter 等
  // 任何 UI-only 鍵。深拷貝是關鍵：淺取 groups 會讓 wire 仍指向帶標記的 rule 物件。所有送往後端的
  // scenario/draft 一律經此，確保 UI-only 標記絕不外洩到 wire（Karpathy：不靜默假設、顯式剝除）。
  function toWireScenario(s) {
    var all = s.groups || [];
    // 預設(I) 群組一律排在所有可編輯組之後 ⇒ 後端 left-fold 把它 AND 到整個情境（情境層級，Option A），
    // 不受使用者「先加 I 再加組」的插入順序影響（否則 I 會只 AND 到前一組而非整體）。
    var ordered = all.filter(function (g) { return !g.__kctPresetGroup; })
      .concat(all.filter(function (g) { return g.__kctPresetGroup; }));
    return {
      name: s.name,
      rationale: s.rationale,
      // 過濾掉沒有條件的群組：使用者「＋ 另一組條件」建到一半的空 set 不送出，後端不會報「群組沒有規則」。
      groups: ordered.filter(function (g) { return (g.rules || []).length > 0; }).map(function (g) {
        return {
          join: g.join,
          rules: (g.rules || []).map(function (r) {
            var clean = {};
            Object.keys(r).forEach(function (k) {
              if (k !== KCT_LETTER_KEY) { clean[k] = r[k]; }
            });
            return clean;
          })
        };
      })
    };
  }

  // 群組組合器：群組內規則 join 的一致值——任一規則為 OR 即「任一(OR)」，否則「全部(AND)」。空群組
  // 回退「全部」。組合器＝把群組內各規則 join 設為同值，後端逐條 join 評估等價（全 AND＝符合全部、全 OR＝任一）。
  function groupCombinator(group) {
    return group.rules.some(function (r) { return r.join === 'OR'; }) ? 'OR' : 'AND';
  }

  // 作用中（active）群組：UI-only 標記 __active，標示「上方面板新增的條件要進哪一組」。恆有至多一個非預設
  // 群組帶 __active；toWireScenario 把 group 重建為 {join, rules}，此鍵自然不外洩（同 __kctPresetGroup）。
  // 取作用中組：帶 __active 的非預設組 → 否則回退最後一個非預設組 → 再無則 null。
  function activeEditableGroup(draft) {
    var editable = (draft.groups || []).filter(function (g) { return !g.__kctPresetGroup; });
    var active = null;
    editable.forEach(function (g) { if (g.__active) { active = g; } });
    if (active) { return active; }
    return editable.length ? editable[editable.length - 1] : null;
  }

  // 設作用中：先清掉所有 __active，再標記目標（非預設組）。傳 null 僅清空。
  function setActiveGroup(draft, group) {
    (draft.groups || []).forEach(function (g) { if (g.__active) { delete g.__active; } });
    if (group && !group.__kctPresetGroup) { group.__active = true; }
  }

  // 某組內的 KCT 字母（依 checklist A→J 排序、去重）。
  function kctLettersInGroup(group) {
    var has = {};
    group.rules.forEach(function (r) { if (r[KCT_LETTER_KEY]) { has[r[KCT_LETTER_KEY]] = true; } });
    return Ui.FILTER_KCT_CHECKLIST
      .filter(function (it) { return has[it.letter]; })
      .map(function (it) { return it.letter; });
  }

  // 某組是否含任何 KCT 規則（帶 __kctLetter）——判斷「結構變動是否涉及 KCT 字母」，決定要不要重算命名
  //（只有涉及 KCT 才重算，避免在純自訂情境誤清手改名稱）。
  function groupHasKct(group) {
    return !!group && group.rules.some(function (r) { return r[KCT_LETTER_KEY]; });
  }

  // KCT 命名（使用者指定，可手改）：單一可編輯組沿用「全部所選字母排一排」（如 G+H、G+I+J），最乾淨；多
  // 可編輯組則以「組」區分——每組字母用 '+' 串、組間用 '｜' 分隔（只有自訂條件的組顯示「自訂」、全空略過），
  // 讓名稱看得出結構；預設(I) 字母附在最後一個 token。例：第1組{G,H}、第2組{J} → G+H｜J；跨組 → G+H｜G+J。
  // KCT 命名（使用者指定，可手改）：每個可編輯組一個字母 token（依 checklist 排序、'+' 串；只有自訂條件的
  // 組顯「自訂」、全空略過）；預設(I) 是情境層級獨立區塊（Option A），命名為「自己的 token」附在最後。token
  // 之間以 '｜' 分隔（單一 token 時不加分隔）。例：G、G+H、G+H｜G+J、G｜H｜I、G｜I、G｜自訂。
  function kctScenarioName(draft) {
    var tokens = [];
    draft.groups.forEach(function (g) {
      if (g.__kctPresetGroup) { return; }
      var letters = kctLettersInGroup(g);
      if (letters.length) { tokens.push(letters.join('+')); }
      else if (g.rules.length) { tokens.push('自訂'); }
    });
    var presetLetters = [];
    draft.groups.forEach(function (g) {
      if (g.__kctPresetGroup) {
        kctLettersInGroup(g).forEach(function (L) { if (presetLetters.indexOf(L) < 0) { presetLetters.push(L); } });
      }
    });
    if (presetLetters.length) { tokens.push(presetLetters.join('+')); }
    return tokens.join('｜');
  }

  // KCT 動機（使用者指定）：每個所選 KCT 卡的詳細說明逐行列出（字母＋清單 label）。
  function kctScenarioRationale(draft) {
    return Ui.FILTER_KCT_CHECKLIST
      .filter(function (item) { return isKctSelected(draft, item); })
      .map(function (item) { return item.letter + '：' + item.label; })
      .join('\n');
  }

  // 勾選/取消 KCT 後重算名稱與動機並寫回草稿：KCT 選取主導命名；無 KCT 規則時清空兩欄，交回手動填寫
  //（純自訂條件的情境）。就地修改傳入 draft。
  function applyKctNaming(draft) {
    var name = kctScenarioName(draft);
    draft.name = name;
    draft.rationale = name ? kctScenarioRationale(draft) : '';
  }

  /* ============================================================================
     區塊 1：KCT條件 選取（A–J 可複選 toggle）
     十顆卡片由 FILTER_KCT_CHECKLIST 一份資料驅動。已選/未選兩態由身分標記得出（isKctSelected：
     草稿中是否存在帶該卡字母標記 __kctLetter 的 rule），不再做結構簽章猜測。
     B（Phase 2 佔位）與需科目配對未匯入的 A/C/D：停用＋原因註記，不綁 toggle。
     ============================================================================ */
  function kctPickerHtml(draft) {
    var available = availableRuleTypes();
    function typeAvailable(ref) {
      return available.some(function (t) { return t.value === ref; });
    }

    var selectedCount = 0;
    var cells = Ui.FILTER_KCT_CHECKLIST.map(function (item) {
      // Phase 2 佔位優先；其次是「需科目配對但未匯入」的鏡像閘門停用。
      var blockedByMapping = item.kind === 'type' && !item.disabled && !typeAvailable(item.ref);
      var disabled = item.disabled || blockedByMapping;
      var note = item.disabled
        ? item.note
        : (blockedByMapping ? '需先匯入科目配對' : '');

      var state = disabled ? 'none' : kctCardState(draft, item);
      if (state === 'selected') { selectedCount++; } // 計數＝作用中組的組層 KCT（情境層級 I 不計入）

      var cls = 'picker-card picker-card--kct' +
        (state === 'selected' ? ' picker-card--selected' : '') +
        (state === 'elsewhere' ? ' picker-card--elsewhere' : '') +
        (state === 'preset' ? ' picker-card--preset' : '') +
        (disabled ? ' picker-card--disabled' : '');
      var pressed = (state === 'selected' || state === 'preset') ? 'true' : 'false';

      return '<button type="button" class="' + cls + '"' +
          ' data-kct-letter="' + item.letter + '"' +
          ' aria-pressed="' + pressed + '"' +
          (disabled ? ' disabled aria-disabled="true"' : '') + '>' +
        '<span class="picker-card__letter" aria-hidden="true">' + Ui.esc(item.letter) + '</span>' +
        '<span class="picker-card__label" title="' + Ui.esc(item.label) + '">' + Ui.esc(item.label) + '</span>' +
        (note ? '<span class="picker-card__note">' + Ui.esc(note) + '</span>' : '') +
        (state === 'elsewhere' ? '<span class="picker-card__elsewhere">也在其他組</span>' : '') +
        (state === 'preset' ? '<span class="picker-card__preset-mark">情境層級</span>' : '') +
      '</button>';
    }).join('');

    return (
      '<section class="condition-picker condition-picker--kct">' +
        '<div class="condition-picker__head">' +
          '<h3 class="condition-picker__title">KCT條件</h3>' +
          '<span class="condition-picker__count">已選 ' + selectedCount + ' 項</span>' +
        '</div>' +
        '<p class="condition-picker__intro">點選方法學檢核清單（A–J）。條件會加到下方「作用中」的組；藍色亮起＝該訊號在作用中組，「也在其他組」＝別組也用了。非營業日(I) 為情境層級（黃標），套用到整個情境。</p>' +
        '<div class="condition-picker__grid condition-picker__grid--kct">' + cells + '</div>' +
      '</section>'
    );
  }

  /* ============================================================================
     區塊 2：自訂篩選條件 選取（四組等寬卡片，點一張＝新增一條可重複的條件）
     等寬/等高靠 CSS grid（condition-picker__grid）統一欄寬；每張卡固定內部結構（mark「＋」加入示意
     ＋label）。仍遵守 availableRuleTypes 鏡像閘門；kct 分組不在此渲染（KCT 有自己的區塊）。
     自訂條件可重複新增，故是「加入」非 toggle，不顯示任何計數（已移除舊版 ×N 徽章）。
     ============================================================================ */
  function customPickerHtml() {
    var available = availableRuleTypes();

    var groupsHtml = Ui.FILTER_RULE_GROUPS.map(function (grp) {
      if (grp.key === 'kct') { return ''; }
      var cards = available.filter(function (t) { return t.group === grp.key; }).map(function (t) {
        return '<button type="button" class="picker-card picker-card--custom"' +
            ' data-action="add-rule" data-rule-type="' + t.value + '">' +
          '<span class="picker-card__mark" aria-hidden="true">＋</span>' +
          '<span class="picker-card__label" title="' + Ui.esc(t.quickLabel || t.label) + '">' + Ui.esc(t.quickLabel || t.label) + '</span>' +
        '</button>';
      }).join('');
      if (!cards) { return ''; }
      return '<div class="condition-picker__group">' +
        '<span class="condition-picker__group-label">' + Ui.esc(grp.label) + '</span>' +
        '<div class="condition-picker__grid condition-picker__grid--custom">' + cards + '</div>' +
      '</div>';
    }).join('');

    return (
      '<section class="condition-picker condition-picker--custom">' +
        '<button type="button" class="condition-picker__toggle" data-action="toggle-custom-picker" aria-expanded="false">' +
          '<span class="condition-picker__toggle-caret" aria-hidden="true">▸</span>' +
          '<span class="condition-picker__title">自訂篩選條件</span>' +
          '<span class="condition-picker__toggle-hint">展開以加入金額、日期、科目配對等自訂條件</span>' +
        '</button>' +
        '<div class="condition-picker__body" data-bind="custom-picker-body" hidden>' +
          '<p class="condition-picker__intro">點卡片新增條件（可重複），會加到下方「作用中」的組。</p>' +
          groupsHtml +
        '</div>' +
      '</section>'
    );
  }

  /* ============================================================================
     區塊 3：建立篩選情境（彙總調整）— 統一條件建構器（單一介面、把分組融入，無模式切換）。
     情境＝一個或多個「條件組(set)」，每組就是一塊淡底 well：頂部 AND/OR 組合器段控（該組 ≥2 條件才顯示）
     ＋條件清單。一組時就是乾淨一塊（看不到「組」字）；「＋ 另一組條件」是常駐普通按鈕（非模式）。≥2 組時
     組間出現一條水平軌道、藍色段控「組間 AND/OR」（父／情境層，與組內中性段控做出層級對比）。一組一種
     組合器，要混就再開組；封頂兩層。非營業日(I) 等預設群組為情境層級獨立區塊，接在所有可編輯組之後、AND 到整個情境。
     新手層：教學空狀態＋行內布林 read-back＋4 拍提示；名稱/動機必填（KCT 選取自動帶入，見 applyKctNaming）。
     兩層 AST 不變；空群組於 toWireScenario 送出時略過（建到一半不報錯）。
     ============================================================================ */

  // 原子預設標籤：把一個預設群組（如 I）呈現為單一白話條件文字。取群組內任一 rule 的 __kctLetter →
  // 該 KCT 卡，再查 FILTER_KCT_ATOM_LABELS（以卡的 ref＝preset key 為鍵），無對映則退回卡 label。
  function presetAtomLabel(group) {
    var letter = null;
    group.rules.some(function (r) {
      if (r[KCT_LETTER_KEY]) { letter = r[KCT_LETTER_KEY]; return true; }
      return false;
    });
    var item = Ui.FILTER_KCT_CHECKLIST.filter(function (k) { return k.letter === letter; })[0];
    if (item && Ui.FILTER_KCT_ATOM_LABELS[item.ref]) { return Ui.FILTER_KCT_ATOM_LABELS[item.ref]; }
    return item ? item.label : '預設條件';
  }

  // 預設(I) 情境層級區塊：唯讀白話＋「情境層級」標籤＋移除；明示它套用到整個情境（與上方條件 AND），不屬於
  // 任何「第 N 組」（非營業日＝週末 OR 假日，結構上是巢狀 OR，2-level 模型只能自成一組）。移除＝splice 整個
  // 預設群組、取消對應 KCT 卡（見 bind 的 remove-preset-group）。
  function presetBlockHtml(group, gi) {
    return (
      '<div class="scenario-preset">' +
        '<div class="scenario-preset__head">' +
          '<span class="scenario-preset__tag">情境層級</span>' +
          '<span class="scenario-preset__label">' + Ui.esc(presetAtomLabel(group)) + '</span>' +
          '<button type="button" class="btn btn--ghost scenario-preset__remove" data-action="remove-preset-group" data-gi="' + gi + '">移除</button>' +
        '</div>' +
        '<p class="scenario-preset__note">套用到整個情境（與上方條件 AND）</p>' +
      '</div>'
    );
  }

  // 組合器段控（segmented control）：兩格 AND/OR、mono、tooltip 給白話、目前值高亮。底層仍是互斥 radio，
  // 沿用既有 data-set-combinator/data-set-join 綁定（值仍為 AND/OR）。modifier 區分組內（中性）與組間
  // （藍色、情境層）。extraAttrs 帶該段控的綁定屬性（data-set-combinator/data-gi 或 data-set-join）。
  function comboSegment(name, current, extraAttrs, modifier) {
    function seg(val, tip) {
      var on = val === current;
      return (
        '<label class="combo-seg__opt' + (on ? ' is-on' : '') + '" title="' + Ui.esc(tip) + '">' +
          '<input type="radio" name="' + name + '" value="' + val + '"' +
            (on ? ' checked' : '') + (extraAttrs ? ' ' + extraAttrs : '') + '>' +
          '<span class="combo-seg__txt">' + val + '</span>' +
        '</label>'
      );
    }
    return (
      '<span class="combo-seg ' + modifier + '">' +
        seg('AND', '每個條件都要成立') +
        seg('OR', '符合任一個就好') +
      '</span>'
    );
  }

  // 一塊 well（一個可編輯條件組）：組合器段控（該組 ≥2 條件才顯示）＋條件清單。multi（≥2 組）時段控放進
  // 組標頭「第 N 組」旁（組內中性段控，與組間藍色段控分層）；single 時段控放 well 頂端、前綴白話 lead
  // 「條件之間」。預設(I) 不再併入 well（改為情境層級獨立區塊，見 presetBlockHtml）。
  function setWellHtml(group, gi, setNumber, multi, isActive) {
    var condCount = group ? group.rules.length : 0;
    var comb = group && group.rules.length ? groupCombinator(group) : 'AND';
    var childAttrs = 'data-set-combinator data-gi="' + gi + '"';
    var segment = (group && condCount >= 2)
      ? comboSegment('set-combinator-' + gi, comb, childAttrs, 'combo-seg--group')
      : '';

    var rows = group ? group.rules.map(function (rule, ri) { return ruleRowHtml(rule, gi, ri); }).join('') : '';

    // 作用中徽章／「點此設為作用中」提示（僅 multi、可編輯組）：標示上方面板新增條件的落點。
    var marker = (multi && group)
      ? (isActive
          ? '<span class="scenario-set__badge">作用中</span>'
          : '<span class="scenario-set__hint">點此設為作用中</span>')
      : '';

    var head = '';
    var chooser = '';
    if (multi) {
      head = '<div class="scenario-set__head">' +
          '<span class="scenario-set__title">第 ' + setNumber + ' 組</span>' + marker + segment +
          (group ? '<button type="button" class="btn btn--ghost scenario-set__remove" data-action="remove-set" data-gi="' + gi + '">移除這組</button>' : '') +
        '</div>';
    } else if (segment) {
      chooser = '<div class="combo-row">' +
          '<span class="combo-row__lead">條件之間</span>' + segment +
        '</div>';
    }

    var cls = 'scenario-flat' + (multi ? ' scenario-set' : '') +
      (multi && isActive ? ' scenario-set--active' : '') +
      (multi && group && !isActive ? ' scenario-set--locked' : '');
    // multi 可編輯組整塊 well 可點＝設作用中（見 bind 的 data-active-target）。
    var activeAttr = (multi && group) ? ' data-active-target data-gi="' + gi + '"' : '';
    var emptyMsg = (multi && group && !isActive)
      ? '先點這組設為作用中，再從上方挑條件加入。'
      : '從上面挑「KCT條件」或「自訂篩選條件」，會加到這一組。';

    return (
      '<div class="' + cls + '"' + activeAttr + '>' +
        head + chooser +
        '<div class="scenario-flat__list">' +
          (rows || '<p class="empty-state">' + emptyMsg + '</p>') +
        '</div>' +
      '</div>'
    );
  }

  // 條件組之間的連接器：做成一條水平軌道（spine），藍色段控置中跨在線上——父／情境層運算子，與組內中性
  // 段控明顯分層。data-set-join 設「所有可編輯群組」的 join 為一致值（單一組間運算子）。name 以 gi 唯一，
  // 避免 3 組以上時多個連接器共用 name 互相撞群（各連接器顯示同一致值即可）。
  function interSetConnectorHtml(joinValue, gi) {
    return (
      '<div class="set-rail">' +
        '<span class="set-rail__lead">組間</span>' +
        comboSegment('set-join-' + gi, joinValue, 'data-set-join', 'combo-seg--scenario') +
      '</div>'
    );
  }

  // 渲染所有「條件組」：每個可編輯群組一塊 well（組間插白話連接器）；預設群組（I）以情境層級獨立區塊呈現，
  // 接在所有可編輯組之後（AND 到整個情境，Option A）。
  function setsHtml(draft) {
    var editable = [];
    var presets = [];
    draft.groups.forEach(function (g, gi) {
      if (g.__kctPresetGroup) { presets.push({ group: g, gi: gi }); }
      else { editable.push({ group: g, gi: gi }); }
    });

    var presetBlocks = presets.map(function (p) { return presetBlockHtml(p.group, p.gi); }).join('');

    // 只有預設群組（如只選了 I）：只呈現情境層級區塊。
    if (editable.length === 0) {
      return presetBlocks;
    }

    var multi = editable.length >= 2;
    var active = activeEditableGroup(draft);
    var interJoin = (editable[1] && editable[1].group.join === 'AND') ? 'AND' : 'OR';
    var wells = editable.map(function (e, idx) {
      var isFirst = idx === 0;
      var connector = isFirst ? '' : interSetConnectorHtml(interJoin, e.gi);
      return connector + setWellHtml(e.group, e.gi, idx + 1, multi, e.group === active);
    }).join('');
    return wells + presetBlocks;
  }

  // 行內布林：把一組條件文字以指定運算子（AND/OR）相連，運算子上色（opClass）。條件逐一 Ui.esc，
  // 運算子為字面 AND/OR（安全）。
  function exprJoin(labels, op, opClass) {
    var opHtml = ' <span class="expr-op ' + opClass + '">' + op + '</span> ';
    return labels.map(function (l) { return Ui.esc(l); }).join(opHtml);
  }

  // 整句回顯（read-back）：句首白話 lead＋行內布林式，把 AND/OR 寫進去（鏡像控制項：OR＝情境層藍粗、
  // AND＝組內灰）。同時可直接作為底稿的條件邏輯。條件文字用 ruleSummaryLabel（index 0 去前綴）＋
  // presetAtomLabel。
  function readBackHtml(draft) {
    var editable = [];
    var presets = [];
    draft.groups.forEach(function (g) {
      if (g.__kctPresetGroup) { presets.push(g); } else { editable.push(g); }
    });
    function groupLabels(group) {
      return group ? group.rules.map(function (r) { return ruleSummaryLabel(r, 0); }) : [];
    }

    // 只看「有條件」的可編輯組：剛按〔＋另一組條件〕還沒填的空組不進 read-back，避免懸空運算子（如 … OR AND …）。
    var ne = editable.filter(function (g) { return g.rules.length > 0; });

    var exprHtml = '';
    if (ne.length === 1) {
      exprHtml = exprJoin(groupLabels(ne[0]), groupCombinator(ne[0]) === 'OR' ? 'OR' : 'AND', 'expr-op--group');
    } else if (ne.length >= 2) {
      var sop = (ne[1] && ne[1].join === 'AND') ? 'AND' : 'OR';
      var parts = ne.map(function (g) {
        var ls = groupLabels(g);
        var inner = exprJoin(ls, groupCombinator(g) === 'OR' ? 'OR' : 'AND', 'expr-op--group');
        return ls.length > 1 ? '（' + inner + '）' : inner;
      });
      exprHtml = parts.join(' <span class="expr-op expr-op--scenario">' + sop + '</span> ');
    }

    // 預設(I)：情境層級、AND 到整個情境（Option A）。附在最後；多組可編輯式先包一層括號再 AND 預設。
    if (presets.length) {
      var andOp = ' <span class="expr-op expr-op--scenario">AND</span> ';
      var presetExpr = presets.map(function (p) { return Ui.esc(presetAtomLabel(p)); }).join(andOp);
      exprHtml = exprHtml
        ? (ne.length >= 2 ? '（' + exprHtml + '）' : exprHtml) + andOp + presetExpr
        : presetExpr;
    }

    if (!exprHtml) { return ''; }

    return (
      '<p class="scenario-readback">' +
        '<span class="scenario-readback__lead">這個情境會找出符合下列邏輯的分錄：</span>' +
        '<span class="scenario-readback__expr">' + exprHtml + '</span>' +
      '</p>'
    );
  }

  // 教學空狀態：無條件時指向上方 palette（NN/g 空狀態三職責：狀態＋學習線索＋指向入口）。
  function teachingEmptyStateHtml() {
    return (
      '<div class="scenario-empty">' +
        '<p class="scenario-empty__title">還沒有任何條件</p>' +
        '<p class="scenario-empty__hint">到上面挑一張「KCT條件」卡，或展開「自訂篩選條件」；挑到的條件會落到這裡讓你設定數值。</p>' +
      '</div>'
    );
  }

  function scenarioBuilderHtml(draft) {
    var totalRules = draft.groups.reduce(function (n, g) { return n + g.rules.length; }, 0);
    var body = totalRules === 0 ? teachingEmptyStateHtml() : setsHtml(draft);
    var readback = totalRules === 0 ? '' : readBackHtml(draft);
    var addSet = totalRules === 0 ? '' :
      '<button type="button" class="btn btn--ghost" data-action="add-set">＋ 另一組條件</button>';

    // ≥2 可編輯組時，全域說明作用中模型（上方面板新增的條件會進「作用中」那組；點組可切換）。
    var editableCount = draft.groups.filter(function (g) { return !g.__kctPresetGroup; }).length;
    var activeHint = (editableCount >= 2)
      ? '<p class="scenario-active-hint">點任一組設為「作用中」，上方面板（KCT條件／自訂篩選條件）新增的條件就會加到該組。</p>'
      : '';

    return (
      '<section class="rule-card scenario-builder">' +
        '<h3 class="rule-card__title">建立篩選情境</h3>' +
        '<p class="scenario-builder__flow">挑訊號 → 設定數值 → 組合 → 命名保存</p>' +
        '<label class="form__row">' +
          '<span class="form__label">情境名稱 <em class="form__req">*</em></span>' +
          '<input class="form__input" type="text" data-bind="scenario-name" placeholder="例：摘要異常且金額偏高" value="' +
            Ui.esc(draft.name) + '">' +
        '</label>' +
        '<label class="form__row">' +
          '<span class="form__label">篩選動機說明 <em class="form__req">*</em></span>' +
          '<textarea class="form__input" rows="4" data-bind="scenario-rationale" placeholder="說明這個情境為何值得保留到工作底稿">' +
            Ui.esc(draft.rationale) + '</textarea>' +
        '</label>' +
        activeHint +
        body +
        readback +
        '<p class="form-notice" data-bind="scenario-notice" role="alert" hidden></p>' +
        '<div class="panel__actions">' +
          addSet +
          '<button type="button" class="btn btn--ghost" data-action="preview-population"' +
            ' title="開啟資料預覽：金額/日期範圍與資料樣貌，作為設定條件的參考">預覽測試母體</button>' +
          '<button type="button" class="btn" data-action="preview-scenario">預覽這個情境</button>' +
          '<button type="button" class="btn" data-action="save-scenario">保存為篩選情境</button>' +
        '</div>' +
      '</section>'
    );
  }

  function ruleRowHtml(rule, gi, ri) {
    // 條件列不再有逐條 AND/OR——群組內的結合改由群組層級的「組合器」統一決定（見 scenarioBuilderHtml）。
    // 型別下拉以同一套四組（optgroup）呈現，與自訂分組一致，強化分類語彙。
    var available = availableRuleTypes();
    var typeOptions = Ui.FILTER_RULE_GROUPS.map(function (grp) {
      var opts = available.filter(function (t) { return t.group === grp.key; }).map(function (t) {
        return '<option value="' + t.value + '"' + (rule.type === t.value ? ' selected' : '') + '>' +
          Ui.esc(t.label) + '</option>';
      }).join('');
      return opts ? '<optgroup label="' + Ui.esc(grp.label) + '">' + opts + '</optgroup>' : '';
    }).join('');

    return (
      '<div class="rule-row" data-gi="' + gi + '" data-ri="' + ri + '">' +
        '<select class="rule-row__type" data-rule-bind="type">' + typeOptions + '</select>' +
        '<div class="rule-row__controls">' + ruleControlsHtml(rule) + '</div>' +
        '<button type="button" class="btn btn--ghost" data-action="remove-rule">移除</button>' +
      '</div>'
    );
  }

  function ruleControlsHtml(rule) {
    function fieldSelect(keys) {
      return '<select data-rule-bind="field">' + keys.map(function (key) {
        return '<option value="' + key + '"' + (rule.field === key ? ' selected' : '') + '>' +
          Ui.esc(Ui.glFieldLabel(key)) + '</option>';
      }).join('') + '</select>';
    }

    function categorySelect(bind, current) {
      return '<select data-rule-bind="' + bind + '">' + Ui.ACCOUNT_CATEGORY_OPTIONS.map(function (o) {
        return '<option value="' + Ui.esc(o.value) + '"' + (current === o.value ? ' selected' : '') + '>' +
          o.label + '</option>';
      }).join('') + '</select>';
    }

    switch (rule.type) {
      case 'prescreen':
        return '<select data-rule-bind="prescreenKey">' + availablePrescreenKeys().map(function (o) {
          return '<option value="' + o.value + '"' + (rule.prescreenKey === o.value ? ' selected' : '') + '>' +
            o.label + '</option>';
        }).join('') + '</select>';

      case 'text':
        return fieldSelect(Ui.FILTER_TEXT_FIELDS) +
          '<input type="text" data-rule-bind="keywords" placeholder="以逗號分隔多個關鍵字" value="' +
            Ui.esc(rule.keywords) + '">' +
          '<select data-rule-bind="mode">' + Ui.TEXT_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.mode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>';

      case 'dateRange':
        return fieldSelect(Ui.FILTER_DATE_FIELDS) +
          '<input type="date" data-rule-bind="from" value="' + Ui.esc(rule.from) + '">' +
          '<span class="rule-row__sep">～</span>' +
          '<input type="date" data-rule-bind="to" value="' + Ui.esc(rule.to) + '">';

      case 'numRange':
        return '<span class="rule-row__field-label" title="不分借貸，以金額大小比較（取絕對值）">金額（絕對值）</span>' +
          '<input type="number" data-rule-bind="from" placeholder="最小金額" value="' + Ui.esc(rule.from) + '">' +
          '<span class="rule-row__sep">～</span>' +
          '<input type="number" data-rule-bind="to" placeholder="最大金額" value="' + Ui.esc(rule.to) + '">';

      case 'drCrOnly':
        return '<select data-rule-bind="drCr">' +
          '<option value="debit"' + (rule.drCr !== 'credit' ? ' selected' : '') + '>僅借方</option>' +
          '<option value="credit"' + (rule.drCr === 'credit' ? ' selected' : '') + '>僅貸方</option>' +
        '</select>';

      case 'manualAuto':
        return '<select data-rule-bind="isManual">' +
          '<option value="true"' + (rule.isManual !== 'false' ? ' selected' : '') + '>人工分錄</option>' +
          '<option value="false"' + (rule.isManual === 'false' ? ' selected' : '') + '>自動分錄</option>' +
        '</select>';

      case 'accountPair':
        return '<select data-rule-bind="pairMode">' + Ui.ACCOUNT_PAIR_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.pairMode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>' +
          '<span class="rule-row__sep">借方</span>' + categorySelect('debitCategory', rule.debitCategory) +
          '<span class="rule-row__sep">貸方</span>' + categorySelect('creditCategory', rule.creditCategory);

      case 'specialAccountCategoryPair':
        // accountPair 的姊妹條件：三模式皆需借方類別(A) 與貸方類別(B) 皆填（否定模式同樣需要
        // 兩類別才能判定「不存在」），故一律呈現兩個 categorySelect，重用同一 helper。
        return '<select data-rule-bind="pairMode">' + Ui.SPECIAL_PAIR_MODE_OPTIONS.map(function (o) {
            return '<option value="' + o.value + '"' + (rule.pairMode === o.value ? ' selected' : '') + '>' +
              o.label + '</option>';
          }).join('') + '</select>' +
          '<span class="rule-row__sep">借方類別(A)</span>' + categorySelect('debitCategory', rule.debitCategory) +
          '<span class="rule-row__sep">貸方類別(B)</span>' + categorySelect('creditCategory', rule.creditCategory);

      case 'periodInOut':
        return '<select data-rule-bind="inPeriod">' +
          '<option value="true"' + (rule.inPeriod !== 'false' ? ' selected' : '') + '>總帳日期在查核期間內</option>' +
          '<option value="false"' + (rule.inPeriod === 'false' ? ' selected' : '') + '>總帳日期在查核期間外</option>' +
        '</select>';

      case 'customKeywords':
        return '<span class="rule-row__field-label">摘要含</span>' +
          '<input type="text" data-rule-bind="keywords" placeholder="以逗號分隔多個關鍵字" value="' +
            Ui.esc(rule.keywords) + '">';

      case 'customTrailingZeros':
        return '<span class="rule-row__field-label" title="金額末幾位連續為 0；位數越多越接近整數">尾數連續 0 位數</span>' +
          '<input type="number" data-rule-bind="digits" min="1" max="12" step="1" value="' +
            Ui.esc(rule.digits) + '">';

      case 'customPreparerEntryCount':
        return '<span class="rule-row__field-label">編製人員全期張數 ≤</span>' +
          '<input type="number" data-rule-bind="maxEntries" min="1" step="1" value="' +
            Ui.esc(rule.maxEntries) + '">';

      case 'customAccountEntryCount':
        return '<span class="rule-row__field-label">科目全期張數 ≤</span>' +
          '<input type="number" data-rule-bind="maxEntries" min="1" step="1" value="' +
            Ui.esc(rule.maxEntries) + '">';

      case 'revenueDebitNearQuarterEnd':
        return '<span class="rule-row__field-label" title="總帳(過帳)日落在曆年季底前 N 天，且科目為收入、在借方側">季末前</span>' +
          '<input type="number" data-rule-bind="windowDays" min="1" max="92" step="1" placeholder="天數" value="' +
            Ui.esc(rule.windowDays) + '">' +
          '<span class="rule-row__sep">天・借記收入</span>';

      case 'revenueWithoutNormalCounterpart':
        return '<span class="rule-row__field-label" title="貸方為收入，但同傳票無應收/預收的借方分錄">貸收入・借方非應收/預收</span>';

      case 'manualRevenueEntry':
        return '<span class="rule-row__field-label" title="科目為收入且為人工分錄">收入・人工分錄</span>';

      case 'trailingDigits':
        // 範例提示改常駐 helper text（NN/g、GOV.UK：hover-only/title 對鍵盤/觸控/報讀器不友善；
        // 欄位有預設值 000000，placeholder 不可用，故範例放欄位下方常駐一行）。
        return '<span class="rule-row__field-label" title="顯示金額整數尾數符合任一樣態（捨小數）">金額尾數為</span>' +
          '<span class="rule-field">' +
            '<input type="text" data-rule-bind="keywords" value="' + Ui.esc(rule.keywords) + '">' +
            '<span class="rule-field__hint">例：999999 或 000000</span>' +
          '</span>';

      case 'preparerEqualsApprover':
        return '<span class="rule-row__field-label" title="同一張傳票的建立人員與核准人員相同">編製＝核准同一人</span>';

      default:
        return '';
    }
  }

  function ruleSummaryLabel(rule, index) {
    var prefix = index === 0 ? '' : '(' + (rule.join === 'OR' ? 'OR' : 'AND') + ') ';
    switch (rule.type) {
      case 'prescreen': {
        var hit = Ui.PRESCREEN_KEY_OPTIONS.filter(function (o) { return o.value === rule.prescreenKey; })[0];
        return prefix + '預篩選：' + (hit ? hit.label : rule.prescreenKey);
      }
      case 'text': {
        var mode = Ui.TEXT_MODE_OPTIONS.filter(function (o) { return o.value === rule.mode; })[0];
        return prefix + Ui.glFieldLabel(rule.field) + ' ' + (mode ? mode.label : rule.mode) +
          '「' + rule.keywords + '」';
      }
      case 'dateRange':
        return prefix + Ui.glFieldLabel(rule.field) + ' ' + (rule.from || '…') + '～' + (rule.to || '…');
      case 'numRange':
        return prefix + '金額（絕對值） ' + (rule.from ? '≥ ' + rule.from : '') +
          (rule.from && rule.to ? '、' : '') + (rule.to ? '≤ ' + rule.to : '');
      case 'drCrOnly':
        return prefix + (rule.drCr === 'credit' ? '僅貸方' : '僅借方');
      case 'manualAuto':
        return prefix + (rule.isManual === 'false' ? '自動分錄' : '人工分錄');
      case 'accountPair': {
        var pairMode = Ui.ACCOUNT_PAIR_MODE_OPTIONS.filter(function (o) { return o.value === rule.pairMode; })[0];
        return prefix + '科目配對分析：' + (pairMode ? pairMode.label : rule.pairMode) +
          '（借方 ' + rule.debitCategory + '・貸方 ' + rule.creditCategory + '）';
      }
      case 'specialAccountCategoryPair': {
        var specialMode = Ui.SPECIAL_PAIR_MODE_OPTIONS.filter(function (o) { return o.value === rule.pairMode; })[0];
        return prefix + '特殊科目配對：借 ' + rule.debitCategory + '／貸 ' + rule.creditCategory +
          '（' + (specialMode ? specialMode.label : rule.pairMode) + '）';
      }
      case 'periodInOut':
        return prefix + (rule.inPeriod === 'false' ? '總帳日期在查核期間外' : '總帳日期在查核期間內');
      case 'customKeywords':
        return prefix + '自訂關鍵字「' + rule.keywords + '」';
      case 'customTrailingZeros':
        return prefix + '尾數連續 ' + rule.digits + ' 個 0';
      case 'customPreparerEntryCount':
        return prefix + '編製人員全期張數 ≤ ' + rule.maxEntries;
      case 'customAccountEntryCount':
        return prefix + '科目全期張數 ≤ ' + rule.maxEntries;
      case 'revenueDebitNearQuarterEnd':
        return prefix + '季末前 ' + (rule.windowDays || '…') + ' 天借記收入';
      case 'revenueWithoutNormalCounterpart':
        return prefix + '貸收入・借方非應收/預收';
      case 'manualRevenueEntry':
        return prefix + '收入之人工分錄';
      case 'trailingDigits':
        return prefix + '金額尾數「' + rule.keywords + '」';
      case 'preparerEqualsApprover':
        return prefix + '編製＝核准同一人';
      default:
        return prefix + rule.type;
    }
  }

  function scenarioPillsHtml(groups) {
    return (groups || []).map(function (group, gi) {
      var pills = group.rules.map(function (rule, ri) {
        return '<span class="scenario-pill">' + Ui.esc(ruleSummaryLabel(rule, ri)) + '</span>';
      }).join('');
      var head = gi === 0 ? '' :
        '<span class="scenario-pill scenario-pill--join">' + (group.join === 'OR' ? 'OR' : 'AND') + '</span>';
      return head + '<span class="scenario-pill scenario-pill--group">群組 ' + (gi + 1) + '</span>' + pills;
    }).join('');
  }

  function previewPaneHtml(preview) {
    var body;
    if (!preview) {
      body = '<p class="empty-state">先建立規則，再按「預覽這個情境」。</p>';
    } else {
      body =
        '<p class="rule-card__sub">命中 ' + Number(preview.count).toLocaleString() + ' 筆／' +
          Number(preview.voucherCount).toLocaleString() + ' 張傳票；以下為前 ' +
          preview.previewRows.length + ' 筆預覽。</p>' +
        '<div class="preview-table__wrap">' + Ui.previewTableHtml(preview.previewRows) + '</div>';
    }

    return (
      '<section class="rule-card">' +
        '<h3 class="rule-card__title">預覽結果</h3>' +
        body +
      '</section>'
    );
  }

  function savedScenariosHtml(saved) {
    var items = saved.length === 0
      ? '<p class="empty-state">尚未保存任何篩選情境。先預覽，再決定是否保留到工作底稿。</p>'
      : saved.map(function (s, i) {
          return (
            '<article class="saved-scenario">' +
              '<div class="saved-scenario__head">' +
                '<span class="saved-scenario__name">' + (i + 1) + '. ' + Ui.esc(s.name) + '</span>' +
                '<button type="button" class="btn btn--ghost" data-action="toggle-scenario" data-index="' + i +
                  '">詳情</button>' +
                '<button type="button" class="btn btn--ghost" data-action="remove-scenario" data-index="' + i +
                  '">移除</button>' +
              '</div>' +
              '<div class="saved-scenario__body" data-bind="scenario-body-' + i + '" hidden>' +
                '<p class="rule-card__sub">' + Ui.esc(s.rationale) + '</p>' +
                '<div class="scenario-pills">' + scenarioPillsHtml(s.groups) + '</div>' +
                '<div class="saved-scenario__preview" data-bind="scenario-preview-' + i + '"></div>' +
              '</div>' +
            '</article>'
          );
        }).join('');

    return (
      '<section class="rule-card">' +
        '<h3 class="rule-card__title">已儲存篩選情境' +
          '<span class="check-item__count">' + saved.length + ' / 10</span></h3>' +
        items +
      '</section>'
    );
  }

  /* ---- 高風險條件矩陣（D2 預覽：step3 摘要 + step4 傳票矩陣 + step4-1 行層） ----
     全程零商業邏輯：只把 wire 回來的 matchedPositions 對映成 C 欄 ✓／空白、組表；
     查詢/pivot/計算/SQL 全在後端三 action（tagMatrixScenarios/VoucherPage/RowPage）。 */

  // 摺疊外殼；展開時惰性載入(bind() 的 toggle 處理)。無已存情境時不顯示展開鈕,直接友善空狀態。
  function tagMatrixHtml(saved) {
    if (saved.length === 0) {
      return (
        '<section class="rule-card">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<p class="empty-state">尚未保存任何篩選情境；先在上方保存情境，矩陣會把每個情境當成一個高風險條件欄（C1..CN）。</p>' +
        '</section>'
      );
    }

    return (
      '<section class="rule-card">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<button type="button" class="btn btn--ghost" data-action="toggle-matrix">展開矩陣</button>' +
        '</div>' +
        '<p class="rule-card__sub">把全部已存情境當成高風險條件 C1..CN：情境摘要（各條件命中數）、' +
          '傳票矩陣（每張命中傳票打 ✓ 哪些條件）、行層明細（逐行 tag）。皆由後端即時算出，前端只顯示。</p>' +
        '<div class="saved-scenario__body" data-bind="matrix-body" hidden>' +
          '<div data-bind="matrix-scenarios"></div>' +
          '<div data-bind="matrix-vouchers"></div>' +
          '<div data-bind="matrix-rows"></div>' +
        '</div>' +
      '</section>'
    );
  }

  // 由情境摘要組「C 欄定義」：依 position 升冪,每欄 { position, label:'C{p}', name }。
  // C 欄欄序與標頭一律以此為準(brief：依 tagMatrixScenarios 回的 position 升冪)。
  function buildTagColumns(scenarios) {
    return (scenarios || [])
      .slice()
      .sort(function (a, b) { return a.position - b.position; })
      .map(function (s) {
        return { position: s.position, label: 'C' + s.position, name: s.name };
      });
  }

  // 情境摘要表(step3 交叉參考)：位置/條件名稱(=C 欄)/傳票命中數/行命中數。
  function scenarioSummaryTableHtml(columns) {
    var rows = columns.map(function (c) {
      var s = c.summary;
      return (
        '<tr>' +
          '<td>' + Ui.esc(c.label) + '</td>' +
          '<td>' + Ui.esc(s.name) + '</td>' +
          '<td class="preview-table__amount">' + Number(s.voucherHitCount).toLocaleString() + '</td>' +
          '<td class="preview-table__amount">' + Number(s.rowHitCount).toLocaleString() + '</td>' +
        '</tr>'
      );
    }).join('');

    return (
      '<h4 class="rule-card__title">情境摘要</h4>' +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>欄</th><th>高風險條件（情境名稱）</th><th>命中傳票數</th><th>命中行數</th></tr></thead>' +
          '<tbody>' + rows + '</tbody>' +
        '</table>' +
      '</div>'
    );
  }

  // 動態 C 欄的標頭片段:C{p},以情境名稱當 tooltip(title)。
  function tagColumnHeadHtml(columns) {
    return columns.map(function (c) {
      return '<th title="' + Ui.esc(c.name) + '">' + Ui.esc(c.label) + '</th>';
    }).join('');
  }

  // 把 matchedPositions(陣列)對映成每個 C 欄的 ✓／空白 column 取值函式(供 appendRowsToTbody)。
  // 純對映:matchedPositions 含該欄 position → '✓',否則空白。不做任何查詢/計算。
  function tagCellColumns(columns) {
    return columns.map(function (c) {
      return { cell: function (row) {
        var hit = (row.matchedPositions || []).indexOf(c.position) >= 0;
        return hit ? '✓' : '';
      }, className: 'preview-table__tag' };
    });
  }

  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('進階條件篩選');
      Ui.bindNoProjectPanel(container);
      return;
    }

    var draft = state.filter.draft;
    var saved = state.filter.savedScenarios;
    var prescreenRun = state.lastRuns.prescreen;
    var draftRuleCount = draft.groups.reduce(function (sum, g) { return sum + g.rules.length; }, 0);

    container.innerHTML =
      '<div class="panel panel--wide">' +
        '<h2 class="panel__title">進階條件篩選</h2>' +
        '<p class="panel__hint">挑選 KCT 方法學條件或自訂條件，彙整成一個「篩選情境」，最多保存 10 個；' +
          '所有條件一律由後端在資料庫中計算，前端只負責組裝與顯示。</p>' +
        '<div class="stats-bar">' +
          '<div class="stat-card">' +
            '<span class="stat-card__value">' + (prescreenRun ? '已執行' : '未執行') + '</span>' +
            '<span class="stat-card__label">風險預篩選</span>' +
          '</div>' +
          '<div class="stat-card">' +
            '<span class="stat-card__value">' + draftRuleCount + '</span>' +
            '<span class="stat-card__label">草稿規則數</span>' +
          '</div>' +
          '<div class="stat-card">' +
            '<span class="stat-card__value">' + saved.length + ' / 10</span>' +
            '<span class="stat-card__label">已儲存情境</span>' +
          '</div>' +
        '</div>' +
        kctPickerHtml(draft) +
        customPickerHtml() +
        scenarioBuilderHtml(draft) +
        previewPaneHtml(state.filter.preview) +
        savedScenariosHtml(saved) +
        tagMatrixHtml(saved) +
        '<div class="panel__actions">' + Ui.MOCK_BUTTON_HTML + '</div>' +
        Ui.stepFooterHtml(state) +
      '</div>';

    bind(container);
  }

  // 預覽／保存前的前端引導：名稱、動機、至少一條件未補齊時，於欄位旁（紅框＋紅字）
  // 與按鈕上方（form-notice）就近提示並不送出。純呈現——後端 invalid_scenario 仍是權威。
  // 名稱/動機一律必填（已退場 source:'kct' 的免填特例）。
  function scenarioGate(container) {
    var draft = Store.getState().filter.draft;
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    var notice = container.querySelector('[data-bind="scenario-notice"]');

    Ui.clearFieldError(nameInput);
    Ui.clearFieldError(rationaleInput);

    var nameEmpty = !draft.name || !draft.name.trim();
    var rationaleEmpty = !draft.rationale || !draft.rationale.trim();
    var noCondition = draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; });

    var problems = [];
    if (nameEmpty) { Ui.setFieldError(nameInput, '請先填寫情境名稱'); problems.push('情境名稱'); }
    if (rationaleEmpty) { Ui.setFieldError(rationaleInput, '請先填寫篩選動機說明'); problems.push('篩選動機'); }
    if (noCondition) { problems.push('至少一條篩選條件'); }

    if (problems.length === 0) {
      if (notice) { notice.hidden = true; notice.textContent = ''; }
      return true;
    }
    if (notice) {
      notice.textContent = '尚需補齊：' + problems.join('、');
      notice.hidden = false;
    }
    // 聚焦第一個有問題的欄位（名稱 → 動機）；只缺條件時不搶焦點。
    if (nameEmpty) { nameInput.focus(); }
    else if (rationaleEmpty) { rationaleInput.focus(); }
    return false;
  }

  // 使用者輸入時放寬（NN/g：別在打字途中責備）——只清除已補齊欄位的錯誤、
  // 全部補齊時收起按鈕旁提示；不在此新增任何錯誤。
  function softRefreshGate(container) {
    var draft = Store.getState().filter.draft;
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    var notice = container.querySelector('[data-bind="scenario-notice"]');

    if (draft.name && draft.name.trim()) { Ui.clearFieldError(nameInput); }
    if (draft.rationale && draft.rationale.trim()) { Ui.clearFieldError(rationaleInput); }

    var noCondition = draft.groups.length === 0 ||
      draft.groups.every(function (g) { return g.rules.length === 0; });
    var stillMissing = (!draft.name || !draft.name.trim()) ||
      (!draft.rationale || !draft.rationale.trim()) || noCondition;
    if (!stillMissing && notice) { notice.hidden = true; notice.textContent = ''; }
  }

  // 規則「值編輯」後即時刷新藍色 read-back：值編輯走 patchFilterRule（只 notify、不重建面板以保住輸入
  // 焦點），故 read-back（整段算好的 HTML）不會自己更新——這裡只抽換 .scenario-readback 這一段，不碰
  // 任何輸入框，焦點不受影響（read-back 是條件清單之後的獨立節點）。
  function softRefreshReadback(container) {
    var el = container.querySelector('.scenario-readback');
    if (!el) { return; }
    var html = readBackHtml(Store.getState().filter.draft);
    if (html) { el.outerHTML = html; }
    else if (el.parentNode) { el.parentNode.removeChild(el); }
  }

  function bind(container) {
    Ui.bindMockButton(container, 'filter');
    Ui.bindStepFooter(container);

    // 名稱／動機：值編輯只 patch（notify），不重建面板（保住輸入焦點）。
    var nameInput = container.querySelector('[data-bind="scenario-name"]');
    nameInput.addEventListener('input', function () {
      Store.patchFilterDraftMeta({ name: nameInput.value });
      softRefreshGate(container);
    });
    var rationaleInput = container.querySelector('[data-bind="scenario-rationale"]');
    rationaleInput.addEventListener('input', function () {
      Store.patchFilterDraftMeta({ rationale: rationaleInput.value });
      softRefreshGate(container);
    });

    // 自訂條件卡：點一張＝新增一條該型別條件（可重複；併入「作用中」群組 activeEditableGroup；沒有可編輯
    // 組就先開一組、組合器預設「全部(AND)」並設為作用中）。新規則 join 取該組現有組合器以維持群組內一致。
    container.querySelectorAll('[data-action="add-rule"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var target = activeEditableGroup(draft);
        if (!target) {
          target = { join: 'AND', rules: [] };
          draft.groups.push(target);
          setActiveGroup(draft, target);
        }
        var rule = Ui.newFilterRule(btn.getAttribute('data-rule-type'));
        rule.join = target.rules.length ? groupCombinator(target) : 'AND';
        target.rules.push(rule);
        Store.setFilterDraft(draft);
      });
    });

    // KCT條件卡（A–J）：可複選 toggle —— 已選則移除、未選則加入；累積成同一情境。
    // 停用卡（B/未匯入科目配對）不綁事件。加入/移除/已選偵測全收斂於上方少數深函式。
    container.querySelectorAll('.picker-card--kct').forEach(function (btn) {
      if (btn.disabled) { return; }
      btn.addEventListener('click', function () {
        var item = Ui.FILTER_KCT_CHECKLIST.filter(function (k) {
          return k.letter === btn.getAttribute('data-kct-letter');
        })[0];
        if (!item) { return; }
        var draft = Store.getState().filter.draft;
        if (isKctSelectedActive(draft, item)) {
          // 取消：單規則只從「作用中組」移除（同訊號在別組不動）；預設(I) 是獨立群組、整組移除。
          if (isPresetNewGroup(item)) { removeKctFromDraft(draft, item); }
          else { removeKctFromActiveGroup(draft, item); }
          applyKctNaming(draft);
          Store.setFilterDraft(draft);
          Store.addMessage('已移除 KCT 條件「' + item.label + '」。', 'info');
        } else {
          addKctToDraft(draft, item); // 單規則→作用中組；預設(I)→自成一組
          applyKctNaming(draft);
          Store.setFilterDraft(draft);
          Store.addMessage('已加入 KCT 條件「' + item.label + '」。', 'info');
        }
      });
    });

    // 「＋ 另一組條件」：新增一個可編輯群組（一個 set）。組間預設「符合任一組即可(OR)」（HubSpot 慣例）。
    // 只在已有條件時才出現（見 scenarioBuilderHtml），故綁定前判空。
    var addSetBtn = container.querySelector('[data-action="add-set"]');
    if (addSetBtn) {
      addSetBtn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var fresh = { join: 'OR', rules: [] };
        draft.groups.push(fresh);
        setActiveGroup(draft, fresh); // 新組即新落點：設為作用中
        Store.setFilterDraft(draft);
      });
    }

    // 換位到資料預覽：設定條件前掌握測試母體的數值/日期/摘要樣貌
    var populationBtn = container.querySelector('[data-action="preview-population"]');
    if (populationBtn && Ui.openDataPreview) {
      populationBtn.addEventListener('click', function () {
        Ui.openDataPreview('glEntries');
      });
    }

    // 「移除這組」：splice 該可編輯群組（一個 set）。移到只剩一組會自動回乾淨單 well（無模式）。
    // 移除作用中組時重新標記作用中（回退到最後一組），確保恆有一個有效落點。
    container.querySelectorAll('[data-action="remove-set"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var gi = Number(btn.getAttribute('data-gi'));
        var removedHasKct = groupHasKct(draft.groups[gi]);
        draft.groups.splice(gi, 1);
        setActiveGroup(draft, activeEditableGroup(draft));
        if (removedHasKct) { applyKctNaming(draft); } // 移除含 KCT 的組才重算命名
        Store.setFilterDraft(draft);
      });
    });

    // 點條件組的「中性區域」（非表單控制項、非按鈕）＝把該組設為作用中（上方面板新增的落點）。排除
    // button/input/select/textarea/label：避免攔截編輯、避免重繪奪焦；已是作用中或預設組則不動。
    container.querySelectorAll('[data-active-target]').forEach(function (well) {
      well.addEventListener('click', function (e) {
        if (e.target.closest('button, input, select, textarea, label')) { return; }
        var draft = Store.getState().filter.draft;
        var group = draft.groups[Number(well.getAttribute('data-gi'))];
        if (!group || group.__kctPresetGroup || group.__active) { return; }
        setActiveGroup(draft, group);
        Store.setFilterDraft(draft);
      });
    });

    // 移除原子預設行（如非營業日 I）：splice 整個預設群組、重算 KCT 命名（對應 KCT 卡因 __kctLetter 消失而取消）。
    container.querySelectorAll('[data-action="remove-preset-group"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        draft.groups.splice(Number(btn.getAttribute('data-gi')), 1);
        applyKctNaming(draft);
        Store.setFilterDraft(draft);
      });
    });

    // 自訂篩選條件區塊折疊：切換 body 的 hidden 與 caret（同 toggle-matrix/toggle-scenario 模式，不重建面板）。
    var customToggle = container.querySelector('[data-action="toggle-custom-picker"]');
    if (customToggle) {
      customToggle.addEventListener('click', function () {
        var body = container.querySelector('[data-bind="custom-picker-body"]');
        if (!body) { return; }
        body.hidden = !body.hidden;
        customToggle.setAttribute('aria-expanded', body.hidden ? 'false' : 'true');
        var caret = customToggle.querySelector('.condition-picker__toggle-caret');
        if (caret) { caret.textContent = body.hidden ? '▸' : '▾'; }
      });
    }

    // 條件組組合器（段控，每組一個）：設「該可編輯群組(data-gi)」各 rule 的 join＝符合全部/任一。
    // 預設(I) 群組是情境層級、固定 AND（不再隨組合器同步，見 toWireScenario/Option A）。
    container.querySelectorAll('[data-set-combinator]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        var value = radio.value;
        var draft = Store.getState().filter.draft;
        var group = draft.groups[Number(radio.getAttribute('data-gi'))];
        if (!group) { return; }
        group.rules.forEach(function (r) { r.join = value; });
        Store.setFilterDraft(draft);
      });
    });

    // 條件組之間的連接器（整句 radio）：單一組間運算子——把所有「非預設」群組的 join 設為一致值
    //（預設群組的 join 由上面 sync-presets 管，不在此動）。
    container.querySelectorAll('[data-set-join]').forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        var value = radio.value;
        var draft = Store.getState().filter.draft;
        draft.groups.forEach(function (g) { if (!g.__kctPresetGroup) { g.join = value; } });
        Store.setFilterDraft(draft);
      });
    });

    container.querySelectorAll('.rule-row').forEach(function (row) {
      var gi = Number(row.getAttribute('data-gi'));
      var ri = Number(row.getAttribute('data-ri'));

      row.querySelector('[data-action="remove-rule"]').addEventListener('click', function () {
        var draft = Store.getState().filter.draft;
        var removed = draft.groups[gi].rules[ri];
        var wasKct = !!(removed && removed[KCT_LETTER_KEY]);
        draft.groups[gi].rules.splice(ri, 1);
        if (wasKct) { applyKctNaming(draft); } // 移除 KCT 條件才重算命名（與卡片 toggle 一致；純自訂不動手改名）
        Store.setFilterDraft(draft);
      });

      row.querySelectorAll('[data-rule-bind]').forEach(function (control) {
        var key = control.getAttribute('data-rule-bind');
        if (key === 'type') {
          // 型別切換是結構變動：以 newFilterRule 重建該列、只保留 join（setFilterDraft 整面重繪）。良性
          // 副作用：fresh 不帶 __kctLetter，故此列若原是某 KCT 卡帶入的，改型別後即自然解除身分——picker
          // 會自動取消該卡已選（isKctSelected 找不到帶該字母標記的 rule）。重建即不殘留舊標記。
          control.addEventListener('change', function () {
            var draft = Store.getState().filter.draft;
            var old = draft.groups[gi].rules[ri];
            var wasKct = !!(old && old[KCT_LETTER_KEY]);
            var fresh = Ui.newFilterRule(control.value);
            fresh.join = old.join;
            draft.groups[gi].rules[ri] = fresh;
            if (wasKct) { applyKctNaming(draft); } // 改型別＝解除 KCT 身分，重算命名（與移除一致）
            Store.setFilterDraft(draft);
          });
          return;
        }
        // 值編輯用 input（即時）：patchFilterRule 只 patch、不重建面板（保住焦點），並即時刷新藍色
        // read-back，讓「這個情境會找出…」隨輸入同步更新（修：原本 change 要等下次重繪才更新）。
        control.addEventListener('input', function () {
          var patch = {};
          patch[key] = control.value;
          Store.patchFilterRule(gi, ri, patch);
          softRefreshReadback(container);
        });
      });
    });

    container.querySelector('[data-action="preview-scenario"]').addEventListener('click', function () {
      if (!scenarioGate(container)) { return; }
      var draft = Store.getState().filter.draft;

      Ui.run('預覽篩選情境', function () {
        // 送出剝除標記後的 wire 形狀（draft 內 KCT rule 帶 __kctLetter，絕不可外洩）。
        return global.JetApi.filterPreview({ scenario: toWireScenario(draft) }).then(function (data) {
          Store.setFilterPreview(data.scenario);
          Store.addMessage('情境預覽：命中 ' + data.scenario.count + ' 筆／' +
            data.scenario.voucherCount + ' 張傳票。', 'info');
        });
      });
    });

    container.querySelector('[data-action="save-scenario"]').addEventListener('click', function () {
      if (!scenarioGate(container)) { return; }
      var current = Store.getState().filter;

      // 已存情境 + 當前草稿都過同一投影（深拷貝剝除 __kctLetter，wire 只 { name, rationale, groups }）。
      // 存入 savedScenarios 的也是這份剝乾淨的形狀，故後續惰性預覽其 groups 不含任何 UI-only 標記。
      var scenarios = current.savedScenarios.map(toWireScenario)
        .concat([toWireScenario(current.draft)]);

      if (scenarios.length > 10) {
        Store.addMessage('最多保存 10 個篩選情境；請先移除既有情境。', 'warn');
        return;
      }

      Ui.run('保存篩選情境', function () {
        return global.JetApi.filterCommit({ scenarios: scenarios }).then(function (data) {
          Store.setSavedScenarios(scenarios);
          Store.addMessage('已保存篩選情境（' + data.savedCount + ' / 10）。', 'info');
        });
      });
    });

    container.querySelectorAll('[data-action="toggle-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = btn.getAttribute('data-index');
        var body = container.querySelector('[data-bind="scenario-body-' + index + '"]');
        if (!body) { return; }

        body.hidden = !body.hidden;
        btn.textContent = body.hidden ? '詳情' : '收合';
        if (body.hidden) { return; }

        // 首次展開時惰性載入該情境的命中預覽（重用既有 filter.preview，顯示前 10 筆）；
        // 以預覽容器的 data-loaded 旗標當快取，重複展開不重抓。
        var preview = body.querySelector('[data-bind="scenario-preview-' + index + '"]');
        if (!preview || preview.getAttribute('data-loaded') === '1') { return; }

        var scenario = Store.getState().filter.savedScenarios[Number(index)];
        if (!scenario) { return; }

        // scenarioPosition 對齊後端 1-based position(保存順序);用於 query.filterHitsPage 接續行層明細。
        var scenarioPosition = Number(index) + 1;

        preview.textContent = '載入中…';
        Ui.run('預覽已儲存情境', function () {
          // savedScenarios 保存時即經 toWireScenario 剝乾淨；此處再過一次同一投影，保證送出形狀一致。
          return global.JetApi.filterPreview({
            scenario: toWireScenario(scenario)
          }).then(function (data) {
            var s = data.scenario;
            var rows = s.previewRows.slice(0, 10);
            preview.innerHTML =
              '<p class="rule-card__sub">命中 ' + Number(s.count).toLocaleString() + ' 筆／' +
                Number(s.voucherCount).toLocaleString() + ' 張傳票，以下為前 ' + rows.length + ' 筆。</p>' +
              '<div class="preview-table__wrap">' + Ui.previewTableHtml(rows) + '</div>' +
              '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more" data-action="hits-load-more">載入更多</button>';
            preview.setAttribute('data-loaded', '1');

            // 「載入更多」逐頁接續命中行層明細。tbody = 預覽表 tbody;欄序與 previewTableHtml 一致。
            // 純呼叫膠水:bindLoadMore 帶 cursor 發 query.filterHitsPage、append 列、到底移除鈕;前端不判命中。
            var tbody = preview.querySelector('.preview-table tbody');
            var moreBtn = preview.querySelector('[data-action="hits-load-more"]');
            Ui.bindLoadMore(moreBtn, function (cursor) {
              return global.JetApi.queryFilterHitsPage({
                scenarioPosition: scenarioPosition, cursor: cursor, pageSize: 200
              });
            }, function (hitRows) {
              Ui.appendRowsToTbody(tbody, hitRows, [
                function (r) { return r.documentNumber; },
                function (r) { return r.lineItem || '—'; },
                function (r) { return r.postDate; },
                function (r) { return (r.accountCode || '') + ' ' + (r.accountName || ''); },
                function (r) { return r.description || ''; },
                { cell: function (r) { return Number(r.amount).toLocaleString(); },
                  className: 'preview-table__amount' },
                function (r) { return r.drCr === 'DEBIT' ? '借' : '貸'; }
              ]);
            }, function () {
              // 首擊清掉預覽前 10 列(filter.preview 另一套排序),改接 keyset ASC 全量,避免重複與排序不一致。
              // 清列後全列皆來自 filterHits(欄位讀 r.description,與接列一致),預覽的 documentDescription 耦合自然消失。
              tbody.innerHTML = '';
            });
          });
        });
      });
    });

    container.querySelectorAll('[data-action="remove-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        var remaining = Store.getState().filter.savedScenarios
          .filter(function (_, i) { return i !== index; })
          .map(toWireScenario);

        Ui.run('移除篩選情境', function () {
          return global.JetApi.filterCommit({ scenarios: remaining }).then(function () {
            Store.setSavedScenarios(remaining);
            Store.addMessage('已移除情境（剩餘 ' + remaining.length + ' / 10）。', 'info');
          });
        });
      });
    });

    bindTagMatrix(container);
  }

  // 高風險條件矩陣:展開時惰性載入(摘要 → 組 C 欄定義 → 傳票矩陣 + 行層,各自載入更多)。
  // 以 matrix-body 的 data-loaded 旗標當快取,重複展開不重抓。零商業邏輯:只發 action、組表、對映 ✓。
  function bindTagMatrix(container) {
    var toggle = container.querySelector('[data-action="toggle-matrix"]');
    if (!toggle) { return; }

    toggle.addEventListener('click', function () {
      var body = container.querySelector('[data-bind="matrix-body"]');
      if (!body) { return; }

      body.hidden = !body.hidden;
      toggle.textContent = body.hidden ? '展開矩陣' : '收合矩陣';
      if (body.hidden || body.getAttribute('data-loaded') === '1') { return; }

      var scenariosBox = body.querySelector('[data-bind="matrix-scenarios"]');
      var vouchersBox = body.querySelector('[data-bind="matrix-vouchers"]');
      var rowsBox = body.querySelector('[data-bind="matrix-rows"]');
      scenariosBox.textContent = '載入中…';
      vouchersBox.textContent = '';
      rowsBox.textContent = '';

      Ui.run('載入高風險條件矩陣', function () {
        return global.JetApi.queryTagMatrixScenarios({}).then(function (data) {
          var columns = buildTagColumns(data.scenarios || []);
          // 把摘要掛回欄定義供摘要表顯示名稱與命中數。
          columns.forEach(function (c) {
            c.summary = (data.scenarios || []).filter(function (s) { return s.position === c.position; })[0];
          });

          if (columns.length === 0) {
            scenariosBox.innerHTML =
              '<p class="empty-state">尚無可用情境;請先在上方保存篩選情境。</p>';
            return;
          }

          scenariosBox.innerHTML = scenarioSummaryTableHtml(columns);
          renderVoucherMatrix(vouchersBox, columns);
          renderRowMatrix(rowsBox, columns);
          body.setAttribute('data-loaded', '1');
        });
      });
    });
  }

  // 傳票矩陣(step4):固定欄(傳票號/總帳日/編製者/傳票總額)+ 動態 C 欄;首屏即接第一頁,載入更多續接。
  // 矩陣為全新表(無預覽列混排),故首擊不需清預覽(bindLoadMore 的 clearTarget 省略)。
  function renderVoucherMatrix(box, columns) {
    box.innerHTML =
      '<h4 class="rule-card__title">傳票矩陣（step4：每張命中傳票打 ✓ 哪些條件）</h4>' +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>傳票號碼</th><th>總帳日期</th><th>編製人員</th>' +
            '<th>傳票總額</th>' + tagColumnHeadHtml(columns) + '</tr></thead>' +
          '<tbody></tbody>' +
        '</table>' +
      '</div>' +
      '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more"' +
        ' data-action="matrix-vouchers-more">載入更多</button>';

    var tbody = box.querySelector('.preview-table tbody');
    var fixedCols = [
      function (r) { return r.documentNumber; },
      function (r) { return r.postDate; },
      function (r) { return r.createdBy || ''; },
      { cell: function (r) { return Number(r.voucherTotal).toLocaleString(); },
        className: 'preview-table__amount' }
    ];
    var allCols = fixedCols.concat(tagCellColumns(columns));

    Ui.bindLoadMore(box.querySelector('[data-action="matrix-vouchers-more"]'), function (cursor) {
      return global.JetApi.queryTagMatrixVoucherPage({ cursor: cursor, pageSize: 200 });
    }, function (rows) {
      Ui.appendRowsToTbody(tbody, rows, allCols);
    });
  }

  // 行層明細(step4-1):命中傳票之所有行(含未命中行);無 per-voucher 過濾(Task 4 設計)——
  // 整體命中傳票行明細表 + 傳票號欄供對照 + 載入更多。固定欄 + 動態逐行 C tag。
  function renderRowMatrix(box, columns) {
    box.innerHTML =
      '<h4 class="rule-card__title">行層明細（step4-1：命中傳票之所有行，逐行 tag）</h4>' +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>傳票號碼</th><th>項次</th><th>總帳日期</th><th>科目</th>' +
            '<th>金額</th><th>摘要</th>' + tagColumnHeadHtml(columns) + '</tr></thead>' +
          '<tbody></tbody>' +
        '</table>' +
      '</div>' +
      '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more"' +
        ' data-action="matrix-rows-more">載入更多</button>';

    var tbody = box.querySelector('.preview-table tbody');
    var fixedCols = [
      function (r) { return r.documentNumber; },
      function (r) { return r.lineItem || '—'; },
      function (r) { return r.postDate; },
      function (r) { return (r.accountCode || '') + ' ' + (r.accountName || ''); },
      { cell: function (r) { return Number(r.amount).toLocaleString(); },
        className: 'preview-table__amount' },
      function (r) { return r.description || ''; }
    ];
    var allCols = fixedCols.concat(tagCellColumns(columns));

    Ui.bindLoadMore(box.querySelector('[data-action="matrix-rows-more"]'), function (cursor) {
      return global.JetApi.queryTagMatrixRowPage({ cursor: cursor, pageSize: 200 });
    }, function (rows) {
      Ui.appendRowsToTbody(tbody, rows, allCols);
    });
  }

  function mockApplyFilterScenario() {
    var state = Store.getState();
    if (!state.mapping.gl.committed) {
      Store.addMessage('請先完成欄位配對（可用前面步驟的測試案件按鈕）。', 'warn');
      return;
    }

    Ui.run('套用測試案件（進階篩選）', function () {
      var demoScenario;
      return global.JetApi.projectLoadDemo({}).then(function (demo) {
        demoScenario = demo.demoScenario;
        Store.setFilterDraft(JSON.parse(JSON.stringify(demoScenario)));
        // demoScenario 來自後端，本就無 __kctLetter；仍過 toWireScenario 與其餘送出點一致（單一投影）。
        return global.JetApi.filterPreview({ scenario: toWireScenario(demoScenario) });
      }).then(function (data) {
        Store.setFilterPreview(data.scenario);
        return global.JetApi.filterCommit({ scenarios: [toWireScenario(demoScenario)] });
      }).then(function (commit) {
        Store.setSavedScenarios([demoScenario]);
        var preview = Store.getState().filter.preview;
        Store.addMessage(
          '示範情境已預覽並保存（取代既有清單）：命中 ' + preview.count + ' 筆／' +
          preview.voucherCount + ' 張傳票，已儲存 ' + commit.savedCount + ' / 10。', 'info');
      });
    });
  }

  Ui.registerStep('filter', render);
  Ui.registerMockLoader('filter', mockApplyFilterScenario);
})(window);
