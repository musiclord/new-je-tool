# 低頻科目升級設計(子專案 C 補遺)

> **狀態:已實作,待 GUI 驗收。** 本設計是「匯出底稿」里程碑下子專案 **C(編製人員升級)** 的補遺:把 step3 的 **C9「罕用科目（科目張數 ≤ 11）」** 補成一條可當進階篩選列述詞的規則。C 主體已經實作（待 GUI 驗收），但當時只做了編製人員維度（C5/C6）;科目維度的 C9 落在 C 範圍之外、被漏掉了，這份補遺把它補齊。程式已實作完成，自動化測試全綠（本機 628 個通過，SQL Server parity 也實跑過），只剩使用者 GUI 手動驗收還沒做。權威描述已回寫到 `docs/jet-guide.md`、`docs/action-contract-manifest.md`、`docs/development-status.md`、`docs/development-log.md`。本快照保留供決策回查，GUI 驗收後依慣例去掉日期前綴或刪除。
>
> **盤查依據:** `.git/sdd/bcde-scope-draft.md`（修正 #4 把「科目張數」列為衍生旗標）;方法學 step3 C9（`.git/sdd/workpaper-synthesis.md`）;2026-06-21 在 D2 開工前的自我審查，實機重解了兩份 WorkingPaper 樣本（福懋樣本 C9 命中 40 張），並對碼確認 `rareAccounts` 是彙總、不能當列述詞（見 `PrescreenRules.cs:5-6`、`RuleCatalog.cs:44`）。

## 背景與動機

事務所方法學 step3 的 C9「較少使用之科目」是一條真實在用的高風險條件（福懋樣本命中 40 張傳票）:當某科目全年度的分錄筆數過少（少於 12 筆，即 ≤ 11）時，它的分錄比較容易藏匿違規或錯誤，風險較高。

JET 目前只有 `rare_accounts`（規則 R6），它把「較少使用之科目」做 top-50 的**彙總**，而且形狀是 `RuleShape.Aggregate`，**不能當進階篩選的列述詞**（見 `RuleCatalog.cs:44`，`PrescreenRules.cs:5-6` 也明列）。這和 C 開工前 `creator_summary` 的處境一模一樣——當時 C 新增了 `low_frequency_preparer`（R11，RowTag）和彙總並存。本補遺把同一套模式照搬到科目維度，新增 `low_frequency_account`（RowTag），讓 C9 能存成篩選情境，並被 D2 的 tag 矩陣納入。

## 方法學依據

- **C9 低頻科目**:step3 的寫法是「數字欄位【科目張數】值小於（含）11」。門檻是查核員可調的輸入值，方法學樣本用 11（全年少於 12 筆）。
- step3 C9 還含一個子條件「會計科目名稱不包含〔長串例外清單〕」。這部分由使用者在情境裡用既有的 **Text NotContains** 條件來做（`accName` 已經在 `GlFieldWhitelist` 內），再用 AND 組合，本補遺不另做。

## 目標與範圍

### 做

1. **C9「低頻科目」預篩選規則(RowTag)**:命中條件是某 `account_code` 全期分錄筆數 ≤ **固定預設 11**（Domain 常數）。可當進階篩選的列述詞。與既有的 `rare_accounts`（R6 彙總）**並存，不取代**。
2. **「自訂科目張數」進階篩選條件型別**:讓使用者自己輸入門檻 ≤N（照 C6 的 `customPreparerEntryCount` 與 A 的 `customTrailingZeros` 那套雙軌設計）。
3. 前端:預篩選清單新增一條「低頻科目之分錄」，進階篩選新增「自訂科目張數」條件型別（沿用既有規則清單渲染加 D1 載入更多）。

### 不做（移交給別人 / 暫時不需要）

- **不改 `rare_accounts`**（R6 top-50 彙總視圖保留）。step1 全科目匯出屬於 E。
- **不另造 escalation 基礎設施**。命中分錄走 C5/C6 已經驗證過的路徑:存成情境後用 D1 的 `query.filterHitsPage` 取回全部命中（本補遺不另做分頁）。
- **不做匯出 writer 或 step4-1 動態欄位集**（屬於 E），也不做 tag 矩陣（屬於 D2）。
- **不做科目名稱例外清單的專屬 UI**:由既有的 Text NotContains 在情境裡 AND 組合即可。
- 不新增 schema 表或欄位，因為這條規則純讀既有的 `target_gl_entry`。

## 開發原則與邊界(harness — 本 spec 的 Global Constraints)

- **分層**:`Bridge/Form1 → Application → Domain ← Infrastructure`。規則述詞和門檻常數放 Domain;**provider 分支只能在 Infrastructure**。
- **前端零商業邏輯**:前端、Form1、HTML、CSS、JS 都不做驗證、規則、SQL。
- **postMessage 邊界**:`window.chrome.webview.postMessage` 只能在 `jet-api.js` 裡呼叫。
- **規則一律是參數化的集合式 SQL**:識別字只能來自白名單或常數，使用者輸入的門檻一律用參數綁定。用 `DbCommand`，保持 provider 中立。
- **契約先行**:動任何 action 的 wire 形狀前，**先改 `docs/action-contract-manifest.md`**（prescreen response 加一條 row-tag、filter 條件型別加 `customAccountEntryCount`、可篩選鍵清單更新）。`ActionContractTests` 會鎖住回應形狀。
- **雙 provider 等價**:SQLite 和 SQL Server 兩邊同步（規則述詞、whereBuilder、prescreen 倉儲計數）。SQL Server 測試走 LocalDB 閘控，規則和自訂條件都比照 D1/C 加 `[SqlServerFact]` parity（雙 provider recount 等價，沒設連線就乾淨跳過）。
- **不破壞既有失效不變量**:這條規則純讀 `target_gl_entry`，不依賴外部清單，所以沒有 C5 那種 `NOT IN` 對空集合反轉的風險，也不引入新的失效來源。
- **不編輯** WinForms designer 產生的檔案。
- **測試分三層(jet-testing 規範)**:Domain 做純函式單元測試（門檻常數、登錄表不變量、`MaxEntries` 解析）;Application 做驗收測試（用 `HandlerTestHost` 對 manifest wire：固定規則命中要等於獨立 recount、自訂門檻命中要等於 recount）;Infrastructure 做 parity 測試（真 SQLite，SQL Server 走 LocalDB 閘控）。
- **採 TDD**;**不自行 commit**。版本控制由使用者親自下令，subagent 執行階段也不 commit，改用 tree-diff 隔離。

---

## 功能 1:C9「低頻科目」預篩選規則(RowTag)

- **登錄**:`RuleCatalog.cs` 新增 `("low_frequency_account", "lowFrequencyAccount", "低頻科目", "R12", RuleShape.RowTag)`，與 `rare_accounts`（R6 Aggregate）並存（照 `low_frequency_preparer` R11 與 `creator_summary` R5 並存的方式）。
- **wire key**:`PrescreenRuleKeys` 加 `LowFrequencyAccount = "lowFrequencyAccount"` 常數，並放進 `FilterableKeys`（同步更新 `RuleCatalogTests` 的「FilterableKeys == RowTag 集合」不變量）。
- **門檻常數**:Domain 新增 `AccountFrequency.DefaultMaxEntries = 11`（照 `PreparerFrequency.DefaultMaxEntries`;方法學：全年少於 12 筆）。
- **述詞**（`GlRulePredicates`，集合式 SQL，照 `LowFrequencyPreparer` 寫）:
  ```
  g.account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= @maxEntries)
  ```
  門檻取 `AccountFrequency.DefaultMaxEntries`，參數綁定;純 ANSI，兩個 provider 寫法相同。
- **計數 wire**:`PrescreenRunResult` 的**末位**加 `LowFrequencyAccountCount`;handler wire 出 `lowFrequencyAccount { status, count }`。這條規則永遠可執行，因為它沒有外部依賴、不需要閘控（跟 C6 一樣）。
- **escalation**:這條 row-tag 可以放進篩選情境。查核員存一個「低頻科目」情境後，用 D1 的 `query.filterHitsPage` 把全部命中分錄取回（本補遺不另做）。

## 功能 2:「自訂科目張數」進階篩選條件型別

- **型別**:`FilterScenario.cs` 的 `FilterRuleType` 新增 `CustomAccountEntryCount`。
- **欄位重用**:**直接重用既有的 `FilterRuleSpec.MaxEntries`**（`int?`，C6 已經加過）。一條規則只會是一種型別，`customPreparerEntryCount` 和 `customAccountEntryCount` 不會在同一條規則共存，所以不另開欄位。這是 Good Taste：不為了對稱而多加資料。
- **解析**:`FilterScenarioPayloadParser` 對 `customAccountEntryCount` 解析 `MaxEntries`（跟 `customPreparerEntryCount` 一樣）。
- **驗證**:`FilterScenarioValidator.ValidateRule` 新增一個 case，照 `CustomPreparerEntryCount` 寫（要求 `MaxEntries >= 1`）。
- **WHERE 組譯**:`GlFilterWhereBuilder.BuildRule` 新增一個 case，呼叫 `GlRulePredicates.LowFrequencyAccount(command, maxEntries)`（同一條述詞，但門檻改用使用者輸入值）。

## 受影響的現行碼與新增(盤查)

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:prescreen response 加 `lowFrequencyAccount`;filter 條件型別加 `customAccountEntryCount`;可篩選鍵清單加 `lowFrequencyAccount` |
| `Domain/RuleCatalog.cs` | 加 R12 低頻科目(RowTag),與 R6 並存 |
| `Domain/PrescreenRules.cs` | `PrescreenRuleKeys` 加 `LowFrequencyAccount` + 入 `FilterableKeys`;新 `AccountFrequency.DefaultMaxEntries=11` |
| `Domain/PrescreenContracts.cs` | `PrescreenRunResult` 末位加 `LowFrequencyAccountCount` |
| `Domain/FilterScenario.cs` | `FilterRuleType.CustomAccountEntryCount`;validator case(重用 `MaxEntries`) |
| `Application/FilterScenarioPayloadParser.cs` | `customAccountEntryCount` 解析 `MaxEntries` |
| `Infrastructure/GlRulePredicates.cs` | 加 `LowFrequencyAccount(command, maxEntries)` 述詞 |
| `Infrastructure/GlFilterWhereBuilder.cs` | `customAccountEntryCount` case |
| `Infrastructure/{Sqlite,SqlServer}PrescreenRunRepository.cs` | `lowFrequencyAccount` 計數 |
| `Application/PrescreenRunHandler.cs` | `lowFrequencyAccount` wire(永遠可執行) |
| `wwwroot/js/{ui-core.js}` + `steps/{validate-step.js,filter-step.js}` + css | 預篩選清單一條 + 自訂條件型別/鍵選項/摘要(零邏輯) |
| `docs/jet-guide.md` / `development-status.md` / `development-log.md` / `windows-handoff.md` | 落地回寫 + 待驗卡 |

## 新增/更新測試(TDD,三層)

- **Domain**:`RuleCatalog` 要含 R12 RowTag 且 `FilterableKeys` 同步（`RuleCatalogTests` 不變量）;`AccountFrequency.DefaultMaxEntries==11`;`FilterRuleType.CustomAccountEntryCount` 要能解析 `MaxEntries`;validator 對 `MaxEntries>=1` 做邊界值分析（BVA）。
- **Application 驗收**（用 `HandlerTestHost`，對 manifest wire）:
  - C9 固定 row-tag 命中數要等於 demo 獨立 recount（`account_code` 全期 COUNT ≤ 11）。
  - `customAccountEntryCount(@n)` 進階篩選命中要等於 recount（COUNT ≤ n）。
  - C9 row-tag 可以放進情境，並經 `filter.preview` 或（存後）`filterHitsPage` 取回全部（escalation 端到端，照 C5/C6 的測試寫）。
- **Infrastructure/parity**:固定規則加自訂條件在 SQLite 和 SQL Server 上要等價（`[SqlServerFact]`，比照 D1/C 的雙 provider recount 等價，沒設連線就乾淨跳過）。
- **前端**:不寫 JS 業務規則測試;預篩選項目與自訂條件列進 windows-handoff。

## 交付物

| 檔案/位置 | 變更 |
|:--|:--|
| `docs/action-contract-manifest.md` | 一 row-tag + customAccountEntryCount + 可篩選鍵 |
| `src/JET/JET/Domain/*` | RuleCatalog/PrescreenRules/PrescreenContracts/FilterScenario/門檻常數 |
| `src/JET/JET/Infrastructure/*` | 述詞、whereBuilder、prescreen 倉儲計數 |
| `src/JET/JET/Application/*` | PrescreenRunHandler、PayloadParser |
| `src/JET/JET/wwwroot/*` | 預篩選項目 + 自訂條件 |
| `src/JET/tests/JET.Tests/*` | 三層測試 + [SqlServerFact] parity |
| `docs/jet-guide.md`、`development-status.md`、`development-log.md`、`windows-handoff.md` | 回寫 + 待驗卡 |

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需本機 LocalDB 並設 `JET_SQLSERVER_CONNECTION`(閘控)。

## Windows 端待驗任務(落地時寫入 windows-handoff.md)

- 「資料驗證與測試」的預篩選新增「低頻科目之分錄」（命中全期 ≤11 筆科目的分錄）;可以〔檢視〕加載入更多。
- 「進階條件篩選」可以用「自訂科目張數 ≤N」條件（預設 11、最小 1）;〔預覽這個情境〕命中合理;已儲存情境的摘要顯示「科目全期張數 ≤ N」。
- 可以存「低頻科目」情境後，用命中分頁看它的全部分錄（escalation）;而且和科目名稱 Text NotContains 用 AND 組合（重現 step3 C9）也要正常。

## 與其他子專案的邊界

- **C（主體）**:本補遺照搬 C 的 C6（`lowFrequencyPreparer` + `customPreparerEntryCount`）那套雙軌，共用同一個骨架。
- **D1**:要看命中的「全部分錄」就走 D1 的 `filterHitsPage`;本補遺不另做分頁。
- **D2**:這條 row-tag（C9）可以成為多情境 tag 矩陣的條件;本補遺只提供 row-tag。
- **E**:step1 全科目表、step3 C9 條件的呈現，都屬於 E。
