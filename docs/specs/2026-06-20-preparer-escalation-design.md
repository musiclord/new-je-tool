# 編製人員升級設計(子專案 C)

> **狀態:已實作,待 GUI 驗收。** 本設計屬於「匯出底稿」里程碑下的子專案 **C(編製人員升級)**,程式已實作完成,自動化測試全綠(本機 618 個通過,SQL Server parity 也實跑過),只剩使用者的 GUI 手動驗收還沒做。權威描述已回寫到 `docs/jet-guide.md`、`docs/action-contract-manifest.md`、`docs/development-status.md`、`docs/development-log.md`。本快照保留下來供決策回查,GUI 驗收後依慣例去掉日期前綴或刪除。
>
> **盤查依據:** `.git/sdd/bcde-scope-draft.md`(子專案 C);方法學的 step3 C5/C6 與 step1-2 編製人員表(`.git/sdd/workpaper-synthesis.md`)。

## 背景與動機

事務所方法學裡，編製人員測試有兩條高風險條件。**C5「非授權編製人員」** 指分錄是由不在授權清單上的人編製的；**C6「低頻編製者」** 指某人全年編製的分錄筆數過少，這種人編的分錄風險較高。JET 目前只有 `creator_summary`(對應規則 R5），它把編製者做 top-50 的**彙總**，而且它的形狀是 Aggregate（彙總），**不能拿來當進階篩選的列述詞**（見 `RuleCatalog.cs:43`)。所以現況既沒有授權清單的概念，也沒辦法「挑出非授權者並測他的全部分錄」，更沒辦法「依編製張數篩出低頻者」。子專案 C 就是補上這兩條規則，外加授權清單匯入。

「測非授權者的**全部分錄**」過去需要新的基礎設施才做得到，但 **D1 已經做完全量明細分頁**了。所以 C5 只要做成一條可篩選的 row-tag、把它存成篩選情境，就能用 D1 的 `query.filterHitsPage` 把全部命中的分錄取回來。換句話說，C 不需要自己另造一套 escalation 基礎設施。

## 方法學依據

- **C5 非授權編製人員**:不應該出現由非授權人員編製的分錄（系統自動拋轉的除外）。授權清單是查核團隊維護的名單。方法學的 step3 C5 用「`傳票建立人員_JE` 的值不包含〔名單〕」來表達。
- **C6 低頻編製者**:由不常編製分錄的人（全年編製少於 12 筆，即 ≤ 11）所編的分錄，風險較高。方法學 step3 C6 的寫法是「數字欄位【編製人員張數】值小於（含）11」。這個門檻是查核員可以自行調整的輸入值，方法學樣本用的是 11。
- step1-2 的編製人員表（含編製人員、自動或人工、傳票數、金額彙總）目前由 `creator_summary` 供給。本輪不改它的彙總視圖；全名單匯出屬於 E。

## 目標與範圍

### 做

1. **授權編製人員清單匯入**:接受 `.xlsx` 單欄姓名（英文標頭，replace-only 整批替換）。沿用子專案 F 的參考資料匯入模式。新增 `target_authorized_preparer` 表加上 staging 表，用 `IF NOT EXISTS` 建立，**不升 schema 版本**。
2. **C5「非授權編製人員」預篩選規則**(RowTag):條件是 `created_by` 不在授權清單內。以「名單已匯入」做閘控（跟 `unexpectedAccountPair` 需要科目配對才執行是同一種設計）。可當進階篩選的列述詞。
3. **C6「低頻編製者」**:做兩件事。(a) 一條預篩選 row-tag，命中條件是該 `created_by` 全期分錄筆數 ≤ **固定預設 11**（Domain 常數）；(b) 新增一種進階篩選條件型別「自訂編製人員張數」，讓使用者自己輸入門檻 ≤N（跟子專案 A 的 `customTrailingZeros` 同一個模式）。
4. 前端:加上授權清單匯入卡，並顯示這兩條新預篩選規則（沿用既有的規則清單渲染加上 D1 的載入更多）。

### 不做（移交給別人 / 暫時不需要）

- **不改 `creator_summary`**(top-50 彙總視圖保留)。step1-2 的全名單匯出屬於 E。
- **不另造 escalation 基礎設施**。「測全部分錄」靠 C5 的 row-tag 存成情境，再用 D1 的 `filterHitsPage` 取回。
- **不做匯出 writer 或 step1-2 sheet**（屬於 E），也不做 tag 矩陣（屬於 D2）。
- 授權清單只比對姓名，不納入職稱或部門。底稿本來就是手填的。

## 開發原則與邊界(harness — 本 spec 的 Global Constraints)

- **分層**:`Bridge/Form1 → Application → Domain ← Infrastructure`。規則述詞、門檻常數、匯入解析的欄位定義都放 Domain;**provider 分支只能在 Infrastructure**。跨層的介面（store、materializer 之類）放 Domain。
- **前端零商業邏輯**:前端、Form1、HTML、CSS、JS 都不做驗證、規則、SQL、匯入解析。
- **postMessage 邊界**:`window.chrome.webview.postMessage` 只能在 `jet-api.js` 裡呼叫。
- **規則一律是參數化的集合式 SQL**:識別字只能來自白名單或常數，使用者輸入的值（門檻、清單比對）一律用參數綁定。用 DbCommand，保持 provider 中立。
- **Excel 讀取只走 OpenXML SAX**:授權清單的 `.xlsx` 讀取沿用既有的 `OpenXmlSaxTableReader`（跟 F 一樣），不新增讀取器。
- **契約先行**:動任何 action 的 wire 形狀前，**先改 `docs/action-contract-manifest.md`**（新匯入 action、prescreen 的兩個 row-tag、新的 filter 條件型別）。ActionContractTests 會鎖住回應形狀。
- **雙 provider 等價**:SQLite 和 SQL Server 兩邊同步（新表、規則述詞、匯入 store）。SQL Server 測試走 LocalDB 閘控，每個查詢和規則都比照 D1 加上 `[SqlServerFact]` parity 測試。
- **結果失效要保持不變量**:授權清單**重匯入**時，依賴它的 C5 規則結果要失效。沿用既有「重匯入就清結果」的模式：授權清單 replace 時走 `RuleRunResultReset.ClearWithinAsync`（同一交易），讓 prescreen 和 filter 結果重算。這不能破壞 `result_rule_run` 的回放。
- **不編輯** WinForms designer 產生的檔案。
- **測試分三層(jet-testing 規範)**:Domain 做純函式單元測試（清單解析、門檻常數、述詞語意）;Application 做驗收測試（用 `HandlerTestHost` 對 manifest wire：匯入、兩條規則命中要等於獨立 recount、閘控、自訂門檻）;Infrastructure 做 parity 測試（真 SQLite，SQL Server 走 LocalDB 閘控）。
- **採 TDD**;**不自行 commit**。版本控制由使用者親自下令，subagent 執行階段也不 commit，改用 tree-diff 隔離。

---

## 功能 1:授權編製人員清單匯入

- **格式**:`.xlsx` 單欄，英文標頭（例如 `AUTHORIZED_PREPARER`），姓名逐列排下去，replace-only（重匯入就整批換掉）。沿用 F 的 `ImportCalendarFromFileHandler` 與科目配對匯入的範式：`OpenXmlSaxTableReader` 加上欄位關鍵字解析器，再做三態投影。
- **解析**:新增一個 Domain 單欄解析器，照 `AccountMappingColumnResolver` 的關鍵字命中方式寫，但只認姓名欄。空白列略過，姓名用 `TRIM` 正規化後再存。
- **schema**（雙 provider，用 `IF NOT EXISTS` / `IF OBJECT_ID`，不升版）:
  - `staging_authorized_preparer_raw_row`（batch/row_number/row_json，跟其他 staging 表一樣）。
  - `target_authorized_preparer(name TEXT NOT NULL PRIMARY KEY)`（去重；TRIM 後存）。
- **action**:`import.authorizedPreparer.fromFile`（payload 是檔案路徑，只收 `.xlsx`）。匯入後立即投影，replace-only。失效處理:同一交易呼叫 `RuleRunResultReset.ClearWithinAsync`，因為 C5 的命中依賴這份名單。
- **store**:`IAuthorizedPreparerStore`（Domain 介面）加上 Sqlite/SqlServer/Routing 三個實作（提供 `ReplaceAllAsync` 與供規則查詢的方法）。

## 功能 2:C5「非授權編製人員」預篩選規則(RowTag)

- **登錄**:在 `RuleCatalog.cs` 新增 `("non_authorized_preparer", "nonAuthorizedPreparer", "非授權編製人員", "R10", RuleShape.RowTag)`。`PrescreenRuleKeys` 加常數並放進 `FilterableKeys`（`RuleCatalogTests` 的不變量同步更新）。
- **述詞**（`GlRulePredicates`，集合式 SQL）:`created_by IS NOT NULL AND TRIM(created_by) <> '' AND TRIM(created_by) NOT IN (SELECT name FROM target_authorized_preparer)`。空白的 created_by 不命中，因為無從判定它授權與否。
- **閘控**:名單還沒匯入時不執行這條規則（跟 `unexpectedAccountPair` 用 `input.RunUnexpectedAccountPair` / `HasAccountMapping` 的做法一樣）。`PrescreenRunInput` 加一個 `HasAuthorizedPreparers`，handler 依「名單表非空」放行。wire response 在名單未匯入時，這個鍵回 `status="na"`，並用 `naReason` 說明是因為還沒匯入名單。
- **escalation**:這條 row-tag 可以放進篩選情境。查核員存一個「非授權編製人員」情境後，用 D1 的 `query.filterHitsPage` 把**全部命中的分錄**取回來（C 不另做）。

## 功能 3:C6「低頻編製者」（固定規則 + 自訂門檻條件）

- **門檻常數**:Domain 新增 `PreparerFrequency.DefaultMaxEntries = 11`（方法學：全年少於 12 筆）。
- **固定 row-tag**:`RuleCatalog.cs` 新增 `("low_frequency_preparer", "lowFrequencyPreparer", "低頻編製者", "R11", RuleShape.RowTag)`，放進 `FilterableKeys`。述詞:`created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= 11)`（門檻取 `DefaultMaxEntries`，參數綁定）。
- **自訂門檻的進階篩選條件**:`FilterScenario.cs` 的 `FilterRuleType` 新增 `CustomPreparerEntryCount`;`FilterRuleSpec` 加一個 `int? MaxEntries`;`FilterScenarioPayloadParser` 負責解析;`GlFilterWhereBuilder.BuildRule` 加一個 case，組出 `created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= @maxEntries)`（門檻用使用者輸入值，參數綁定，範圍驗證要 ≥1）。這套「固定預設規則 + 自訂條件型別」的雙軌設計，跟 A 的 `customTrailingZeros` 一致。
- 兩者都是 set-based、provider 中立。`PrescreenRunResult` 加一個 `lowFrequencyPreparerCount`;handler wire 出 `lowFrequencyPreparer { status, count }`。

## 受影響的現行碼與新增(盤查)

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:`import.authorizedPreparer.fromFile` + facade;prescreen response 加 `nonAuthorizedPreparer`/`lowFrequencyPreparer`;filter 條件型別加 `customPreparerEntryCount` + 可篩選鍵清單 |
| `Domain/RuleCatalog.cs` | 加 R10 非授權、R11 低頻(皆 RowTag) |
| `Domain/PrescreenRules.cs` | `PrescreenRuleKeys` 加兩鍵 + 入 `FilterableKeys`;新 `PreparerFrequency.DefaultMaxEntries=11` |
| `Domain/PrescreenContracts.cs` | `PrescreenRunResult` 加兩 count;`PrescreenRunInput` 加 `HasAuthorizedPreparers` |
| `Domain/FilterScenario.cs` + `Application/FilterScenarioPayloadParser.cs` | `FilterRuleType.CustomPreparerEntryCount` + `MaxEntries` 解析 |
| `Domain/`(新)授權清單解析器 + `IAuthorizedPreparerStore` | 單欄姓名解析(鏡射 AccountMappingColumnResolver)、store 介面 |
| `Infrastructure/GlRulePredicates.cs` | 加 `NonAuthorizedPreparer()`、`LowFrequencyPreparer(maxEntries)` 述詞 |
| `Infrastructure/GlFilterWhereBuilder.cs` | `customPreparerEntryCount` case |
| `Infrastructure/{Sqlite,SqlServer}PrescreenRunRepository.cs` | 兩規則計數(C5 閘控) |
| `Infrastructure/{Sqlite,SqlServer,ProviderRouting}AuthorizedPreparerStore.cs` | replace/查詢 |
| `Infrastructure/{Jet,SqlServer}ProjectDatabase.cs` | staging + target_authorized_preparer 表(IF NOT EXISTS,不升版) |
| `Application/ImportAuthorizedPreparerFromFileHandler.cs` | 匯入(鏡射 F);失效集 |
| `Application/PrescreenRunHandler.cs` | 兩規則 wire(C5 閘控 na) |
| `AppCompositionRoot.cs` | 註冊 import handler + store |
| `wwwroot/js/jet-api.js` + `steps/*` + css | import action 字串 + 匯入卡 + 兩規則顯示(零邏輯) |
| `docs/jet-guide.md` / `development-status.md` / `development-log.md` / `windows-handoff.md` | 落地回寫 + 待驗卡 |

## 新增/更新測試(TDD,三層)

- **Domain**:`RuleCatalog` 要含這兩條 RowTag 且 `FilterableKeys` 同步;`PreparerFrequency.DefaultMaxEntries==11`;授權清單解析器（單欄姓名命中、空白略過、TRIM）;`FilterRuleType.CustomPreparerEntryCount` 要能解析出 `MaxEntries`。
- **Application 驗收**(用 `HandlerTestHost`，對 manifest wire):
  - 授權清單匯入後 `target_authorized_preparer` 內容正確、是 replace-only;重匯入要清結果（失效）。
  - C5 命中數要等於 demo 獨立 recount（created_by 不在名單）;名單未匯入時回 `status="na"`。
  - C6 固定 row-tag 命中數要等於 recount（COUNT≤11）;`customPreparerEntryCount(@n)` 進階篩選命中要等於 recount（COUNT≤n）。
  - C5 row-tag 可以放進情境，並經 `filter.preview` 或（存後）`filterHitsPage` 把全部取回（escalation 端到端）。
- **Infrastructure/parity**:授權清單匯入、兩條規則、自訂條件在 SQLite 和 SQL Server 上要等價（`[SqlServerFact]`，比照 D1 的雙 provider 走訪與 recount 等價，沒設連線就乾淨跳過）。
- **前端**:不寫 JS 業務規則測試;匯入卡與規則顯示列進 windows-handoff。

## 交付物

| 檔案/位置 | 變更 |
|:--|:--|
| `docs/action-contract-manifest.md` | import action + 兩 row-tag + customPreparerEntryCount |
| `src/JET/JET/Domain/*` | RuleCatalog/PrescreenRules/PrescreenContracts/FilterScenario/解析器/store 介面/門檻常數 |
| `src/JET/JET/Infrastructure/*` | 述詞、whereBuilder、prescreen 倉儲、store、schema 表 |
| `src/JET/JET/Application/*` | import handler、PrescreenRunHandler、PayloadParser |
| `src/JET/JET/AppCompositionRoot.cs` | 註冊 |
| `src/JET/JET/wwwroot/*` | import 卡 + 規則顯示 |
| `src/JET/tests/JET.Tests/*` | 三層測試 + [SqlServerFact] parity |
| `docs/jet-guide.md`、`development-status.md`、`development-log.md`、`windows-handoff.md` | 回寫 + 待驗卡 |

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需本機 LocalDB 並設 `JET_SQLSERVER_CONNECTION`(閘控)。

## Windows 端待驗任務(落地時寫入 windows-handoff.md)

- 「匯入資料」出現授權編製人員清單上傳卡，上傳 `.xlsx` 單欄姓名後是 replace-only;重匯入會讓相關規則結果失效。
- 「資料驗證與測試」的預篩選多出「非授權編製人員」（名單未匯入時顯示未執行/na）和「低頻編製者」（命中全期 ≤11 筆編製者的分錄）;兩者都可以〔檢視〕加上載入更多。
- 「進階條件篩選」可以用「自訂編製人員張數 ≤N」條件;並且可以存「非授權編製人員」情境後，用命中分頁看它的全部分錄（escalation）。

## 與其他子專案的邊界

- **D1**:C5/C6 row-tag 要看「全部分錄」就走 D1 的 `filterHitsPage`;C 不另做分頁。
- **D2**:這兩條 row-tag（C5/C6）可以成為多情境 tag 矩陣的條件;C 只提供 row-tag。
- **E**:step1-2 編製人員表的全名單匯出、step3 C5/C6 條件的呈現，都屬於 E。
- **A/B**:已完成;C 沿用 A 的「固定規則 + 自訂條件」雙軌，以及 B 的失效不變量模式。
