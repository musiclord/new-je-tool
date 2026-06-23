# 匯出底稿 writer 設計（子專案 E1）

> **狀態：已實作，待 GUI 驗收。** 程式碼已落地，本機自動化測試全套 700 個案例通過（0 個失敗、0 個略過）；設定 `JET_SQLSERVER_CONNECTION` 環境變數後，SQL Server 等價性（parity，指同一查詢在兩種資料庫引擎產出一致結果）測試也實跑通過。但有幾項需要在 Windows 端人工驗收，目前尚未驗收、也尚未提交：逐張工作表在 Excel 中目視對齊樣本、以及大母體匯出時不爆記憶體等 GUI 互動行為（驗收任務卡見 `docs/windows-handoff.md`）。
>
> 本子專案是「匯出底稿」這個里程碑（代號 **E**）的第一階段（代號 **E1**）。E1 的範圍是：用 OpenXML 的 SAX 串流方式，寫出一份含 15 張工作表（其中部分為條件式工作表）的 `.xlsx` 匯出底稿，當中包含三類參考資料工作表。欄位配對的匯入（round-trip，指匯出後能再讀回重建配對）延後到第二階段 E2 處理，這是使用者裁定的。E1 依賴先前子專案 A、B、C、C 補遺、D1、D2、F 全部完成。
>
> **盤查依據：** 15 張工作表的結構來自 `.git/sdd/workpaper-synthesis.md`；E 階段的範圍界定來自 `.git/sdd/bcde-scope-draft.md`。此外，2026-06-21 進入 E 階段前做過一次自我審查：在實機上逐 cell 重新比對兩份 WorkingPaper 樣本的全部 15 張表，並逐欄確認每張表的 JET 資料來源（來自 D1 的分頁查詢、D2 的矩陣查詢、以及 F 和 B 的 store）。

## 背景與動機

事務所這套「JE Testing Tool」的最終產出，是一份 `.xlsx` 工作底稿（WorkingPaper），固定命名為 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`。這份底稿包含封面、step1 到 step5 的測試底稿、以及三張「自動化工具」參考資料表。

JET 至今的 `export.*` 系列 action 全是 stub：它們只把工作委派給前端，後端並不實際寫檔。不過前面幾個子專案已經把每張表所需的查詢和資料都準備齊全：

- step1 完整性，來自 D1 的 `completenessDiffPage`；step1-1 借貸不平，來自 D1 的 `docBalancePage`；step2 可靠性，來自 D1 的 `infSamplePage`。
- step3、step4、step4-1 的高風險矩陣，來自 D2 的 `tagMatrixScenarios`、`tagMatrixVoucherPage`、`tagMatrixRowPage`。
- 假期假日資料來自 F 的行事曆 store；科目配對（含「不在 TB 中」的 Not-in-TB 科目）來自 B；欄位配對來自 JET 的欄位配對設定。

E1 要做的，就是把這些資料以串流方式寫成 `.xlsx`。寫出器走 `DocumentFormat.OpenXml` 的 SAX 介面（`OpenXmlWriter`），不可改用 ClosedXML。這是一條鐵律：ClosedXML 僅限用於 dev fixture `DemoWorkbookWriter`。原因是真實母體可達百萬列（百創這個客戶約 140 萬列），必須串流寫出、不可全部載入記憶體。

## 已驗證的資料地基（E 前自我審查結論）

15 張表逐一做過 cell 級對碼，全部都能對上現有的查詢或 store。只有兩個小缺口需要在 E1 補上：

1. **step1-2「分錄編製人員說明」需要完整名單**（含每位編製人員、其傳票數、其金額彙總）。但現行的 `creator_summary` 查詢只取前 50 筆（`SqlitePrescreenRunRepository.ReadCreatorsAsync` 與 `SqlServerPrescreenRunRepository` 都有 `LIMIT 50`）。因此 E1 需要一條不截斷的編製人員查詢。distinct 的 `created_by` 基數是有界的（人員加上自動拋轉的傳票類型，實務上數十到數百），所以回傳完整清單即可，不需要分頁。
2. **step2 的「分錄來源」欄（J 欄，方法學標註為「若有」）**，對應不到 `infSamplePage` 的資料——`InfSampleRow` 並未包含 `source_module`。因此 E1 讓 step2 的這一欄留空（方法學本來就允許「若有」）。這裡刻意不擴充 `infSamplePage`：依 YAGNI 原則，不為單一欄位去改動一條已驗證過的 D1 查詢。

其餘金額一律以 `amount_scaled` 整數承載，顯示值用 `(decimal)scaled / moneyScale` 換算（沿用既有換算方式，這是純算術，不是業務邏輯）。

## 設計決策（data-structure first、消除特例）

### 寫出器是 deep module，sheet 是資料不是分支

15 張表的形狀分四類：封面與鍵值文字、固定 boilerplate、資料表（表頭加上串流的資料列）、以及條件表或手填骨架。如果用淺薄的設計，最後會變成「15 個各自寫一遍 OpenXML 管線的函式，加上一個依 sheet 名分岔的 god switch」。

因此採用 Ousterhout 的 deep module 做法：`WorkpaperWriter` 對外只暴露一個窄介面 `WriteAsync(Stream, WorkpaperContext) → ExportStats`，把所有 OpenXML SAX 細節藏在內部（shared strings inline、styles、merged cells、number formats、串流）。內部共用一組低階原語：`WriteRow(cells)`、`WriteHeaderBlock(lines)`、`WriteMergedCell(range)`、以及 style 註冊。

資料表全部沿用單一原語 `EmitTableSheet`：它由表頭區塊、欄標、加上一個「列來源」委派組成，委派內部以 keyset 方式逐頁向 repo 取資料、再逐列 yield 出 cell 陣列。step1、step1-2、step2、step3、step4、step4-1 以及三張參考表都走這條路，不為每張表各寫一份管線。封面、固定文字、手填骨架則是各自的小 emitter，一次性、inline 寫出，不硬抽成共用。

條件表（step1-3-1，只有在「差異≠0」時才出現）是 orchestration 層的一道 guard：`if (hasNonZeroDiff) emit(...)`。它不是某個 god-function 裡的特例分支。這正是「換個 data structure 讓特例消失」的實例：要 emit 哪些 sheet，由 orchestrator 依資料動態決定；emitter 本身並不知道「我是第幾張表」，也就沒有相關分支。

### 寫出器是查詢的消費者，不重做任何業務邏輯

`WorkpaperWriter` 屬於 Infrastructure 層，注入既有的 ProviderRouting 查詢 repo（completenessDiffPage、docBalancePage、infSamplePage、tagMatrix*），以及各 store（calendar、accountMapping、fieldMapping、全編製人員查詢）。資料表 emitter 對 repo 以 keyset 逐頁取、逐列寫（SAX 串流，不全部載入），因此百萬列的傳票或明細是逐頁流過寫出器的，記憶體用量有界。scaled 到顯示值的換算在寫出器內完成（純算術）。E1 不在寫出器內重算任何規則。

### 正準中文名只有單一事實來源（Domain）

Field Mapping Info（第 13 表）和日後 E2 的 round-trip 都需要一份對照表，把 logical key 對到正準中文名，例如 `docNum` 對 `傳票號碼_JE`、`postDate` 對 `總帳日期_JE`，TB 側則有 `會計科目編號_TB` 等。因此新增 Domain 型別 `GlCanonicalNames`（logical key 對正準中文名）。它是單一資料結構，匯出與日後 round-trip 共用，這樣才能避免維護兩份方向相反的對照。它和既有的 `GlFieldWhitelist`（logical key 對實體欄、用於 SQL 識別字安全）概念相鄰，但兩者分立。

### step2 的金額用借、貸兩欄（使用者裁定）

`infSamplePage` 的 `InfSampleRow` 已經回傳 `DebitScaled` 和 `CreditScaled`，所以 step2 直接寫成兩欄（E 欄借方、F 欄貸方），不另用「借貸代號 + 單欄」的形式。這樣對齊 D1 既有的資料形狀，語意也不含糊。

## 目標與範圍

### 做（E1）

1. **OpenXML SAX 寫出器核心 `WorkpaperWriter`**：deep module，加上 `EmitTableSheet` 原語，以及樣式、合併、數字格式的處理，串流寫出 `.xlsx`。
2. **15 張（含條件式）工作表**，逐表對齊樣本（來源見下方「工作表 → 來源對照」）：封面 ×2、step1、step1-1、step1-2、step1-3、step1-3-1（條件式）、step2、step3、step4、step4-1、step5、Field Mapping Info、假期假日資訊、科目配對資訊。
3. **手填欄位一律留空**：包括 step1-2 的部門／職稱／說明、step1-3 的原因／調節／調節後、step1-3-1 的前期損益金額、step2 的結果 A–G／詳細說明、step4 的 P–U、step5 的內文。
4. **新後端查詢**：不截斷的全編製人員彙總（供 step1-2 用）。
5. **`export.workpaperStream` action**（取代原本的 stub）：輸入 `{ sheets?, outputPath }`（`sheets` 省略代表全部；`outputPath` 由 host 的存檔對話框提供），輸出 `{ ok, bytesWritten, sheetStats }`，以串流方式寫檔。
6. **host 存檔對話框 `host.selectSavePath`**：用 WinForms 的 `SaveFileDialog`，屬 host I/O；預設檔名為 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`。
7. **前端匯出步驟接線**：沿用 export-step.js，提供選表、觸發、完成回饋，前端不含任何商業邏輯。

### 不做（移交 E2 或依 YAGNI 略過）

- **欄位配對的匯入 round-trip**（讀回 Field Mapping Info 重建配對）移交 E2。
- **CAATs `.docx` 的產生與打包**：封面只寫出檔名字串 `{客戶}_CAATS_JE_WP_{yyyymmdd}.docx`，因為那份 docx 是外部文件、由查核員自備，E1 不產生它。
- step1-3-1 前期損益的**自動計算**（B 已裁定為手填）；step5 內文（手填）。
- 不擴充 `infSamplePage`（所以 step2 的「分錄來源」留空）。
- 不改任何既有規則或查詢的語意；寫出器純讀。
- 不做匯出排程、多檔批次、PDF 等。

## 開發原則與邊界（harness — 本 spec 的 Global Constraints）

**分層與既有鐵律**
- 分層為 `Bridge/Form1 → Application → Domain ← Infrastructure`。正準名表和匯出契約型別放 Domain；寫出器與 provider 查詢放 Infrastructure；`export.workpaperStream` 的 handler 編排放 Application；存檔對話框放 Form1/Bridge（host I/O）。
- 前端不含任何商業邏輯；不在 `jet-api.js` 以外呼叫 `postMessage`。
- 寫出器走 `DocumentFormat.OpenXml` 的 SAX（`OpenXmlWriter`）。ClosedXML 僅限 dev fixture，不得用於底稿寫出。
- 串流，不全部載入：資料表以 keyset 逐頁取、逐列寫。
- 契約先行：先改 `docs/action-contract-manifest.md`（把 `export.workpaperStream` 從 stub 改為 Implemented、新增 `host.selectSavePath`、更新 facade），由 `ActionContractTests` 鎖住回應形狀。
- 雙 provider 等價：寫出器讀的查詢走 ProviderRouting；資料來源在 SQLite 與 SQL Server 兩側等價（這已由 D1、D2 的 parity 保證）；E1 新增的全編製人員查詢加上 `[SqlServerFact]` parity 測試。
- 不破壞既有的失效不變量；不編輯 designer 檔。
- 測試分三層（依 jet-testing）：Domain 測正準名表與匯出契約純函式；Application 驗收測 `export.workpaperStream` 寫出的檔可被讀回，且 sheet 數、表頭、關鍵列正確、條件表存在性正確、手填欄為空；Infrastructure 測全編製人員查詢的 parity。寫出器的驗證一律用 OpenXmlSaxTableReader 或 ClosedXML（測試端可用）讀回後斷言，不靠目視。
- 採 TDD；不自行 commit（以 tree-diff 隔離）；所有 subagent 一律用 `claude-opus-4-8`、effort max，無例外（implementer、task-reviewer、fix、final 皆然）。

**工程品味準則（binding — 本里程碑一貫，且本棒最吃重）**
- **data structure first**：寫任何 `if/else` 前先問「換個 data structure 能不能讓這個分支消失」。業務分支保留（例如行事曆與科目配對的不同處理、step2 的借貸兩欄）；補丁分支則是設計失敗的訊號，要先重構（例如「第一張表特例」「head==null 特例」「第一列特例」）。前面提到的「sheet 清單動態化」和「條件表用 orchestration guard」就是消特例的範例。
- **不留沉默假設、不過度設計**：每一行改動都要能追溯到 spec 的某項要求；200 行能解的事不寫成 1000 行抽象。不為「未來」或「彈性」預先加參數或抽象；只用一次的就 inline。
- **deep module、介面難以誤用**：用窄介面（`WriteAsync → stats`）藏寬實作；非測試 LOC 對公開介面表面積的比目標 ≥3:1。不讓 caller 記隱性順序、不讓互斥選項並存、不讓 null 與 empty 語意不同卻都收下。
- **DRY 的門檻是三次**：前兩次重複先忍，等第三個 caller 出現再抽（避免提前抽象後被迫加 if/else）。
- **SRP 配 deep module**：不為了 SRP 把一體的邏輯切成 5 個淺類；OCP、LSP、ISP、DIP 只在有實際變化壓力時才導入（雙 provider 是 spec 明定才導入，其餘不要類推套用）。
- **命名要精準**（`FetchAllCreatorSummary` 勝過 `GetData`）；**註解寫「為什麼」**（為何 SAX 不用 ClosedXML、為何全名單不分頁、為何 step2 用兩欄），不寫「做什麼」；**函式只做一件事**（如果拆開後要看兩處才懂一件事，就別拆）。
- **不順手改鄰近的無關碼**：看到不順眼但與本 task 無關的，記進 progress ledger 交給使用者，不自行改。本次改動產生的孤兒 import 或變數自己清掉，但 task 之前就存在的死碼不碰。
- reviewer 的 taste 問題比照 Critical/Important/Minor 分級：壞的 data structure、過度抽象、與本 task 正交的改動，派 fix 處理；命名或註解可以更好的，記進 ledger 等 final triage。

## 工作表 → 來源對照（逐表內容規格）

> 表頭固定文字、欄標、合併儲存格、數字格式都逐表對齊樣本（以 `Example_福懋…WorkingPaper.xlsx` 為主、`佰鴻` 交叉比對），細節在 plan 或 impl 階段落實。下表只定來源以及自動／手填的邊界。

| # | 工作表 | 來源 | 內容規則 |
|---|---|---|---|
| 1 | 資料預先整理之說明 | 專案 metadata | A1 公司名稱、A2 測試資料期間、A5 固定說明、A6 `請詳:{客戶}_CAATS_JE_WP_{yyyymmdd}.docx` |
| 2 | JE WorkingPaper說明 | 固定文字 | A1「說明:」+ B1 boilerplate(合併 B1:O1) |
| 3 | step1 完整性測試 | `completenessDiffPage`(全科目,逐頁) | 表頭 + 第19列欄標(科目編號/名稱/TB變動(A)/GL本期借貸彙總(C)/差異(B)-(A))+ 全科目列 |
| 4 | step1-1 借貸不平測試 | 固定文字 + `docBalancePage` | 結論文字;**有不平傳票才 emit 例外表**(條件) |
| 5 | step1-2 分錄編製人員說明 | **全編製人員查詢(新)** | 第11列欄標;B 編製人員/C 自動人工(空)/D 傳票數/E 金額彙總;F 部門/G 職稱/H 說明留空 |
| 6 | step1-3 完整性差異說明 | `completenessDiffPage` WHERE diff≠0 | 第16列欄標;科目/差異金額自動;E 原因/F 調節/G 調節後留空 |
| 7 | step1-3-1 完整性差異調節 | diff≠0 科目 + 固定說明 | **條件表(diff≠0 才 emit)**;科目自動;前期損益金額手填;A4–A6 固定調節說明文字 |
| 8 | step2 可靠性測試 | `infSamplePage`(抽樣行) | 表頭跨 49–52 列(含合併);A 樣本號(序)/B 傳票號/C 科目編號/D 名稱/**E 借方·F 貸方**/G 總帳日/H 核准日/I 編製人員/J 來源(空)/K 摘要/L 核准人員;M–S 結果A–G、T 說明留空 |
| 9 | step3 高風險條件彙總 | `tagMatrixScenarios` | 固定測試說明 + C1..CN:B 代號/C 條件描述(情境 name)/D 原因(情境 rationale)/E 符合傳票數(voucherHitCount) |
| 10 | step4 符合高風險條件傳票 | `tagMatrixVoucherPage`(逐頁) | 第11列欄標;A 編號/B 傳票號/C 總帳日/D 編製者/E 傳票總額;F..(C1..CN)以 matchedPositions 標 Y;P–U 手填留空 |
| 11 | step4-1 符合高風險條件傳票明細 | `tagMatrixRowPage`(逐頁) | 第5列欄標(傳票號/項次/總帳日/建立/核准人員/科目/金額/摘要)+ 動態 C*_TAG 欄(僅有行層命中的 position;以 matchedPositions 標 Y) |
| 12 | step5 財務報表關帳後調整 | 固定橫幅 | A1 黃色 boilerplate(合併 A1:R1);其餘手填 |
| 13 | 自動化工具-檔案欄位資訊 | 欄位配對設定 + `GlCanonicalNames` | A1 版本標籤;TB 段 + GL 段:配對前(來源欄)/型態(kind→文字/日期/數字型態)/長度/小數(best-effort,缺則空)/配對後(正準名);K_ 衍生旗標列出 |
| 14 | 自動化工具-假期假日資訊 | 行事曆 store | 固定週末表(Mon–Sun,六日=Y)+ 假日表(DATE_OF_HOLIDAY/HOLIDAY_NAME/IS_HOLIDAY)+ 補班段 |
| 15 | 自動化工具-科目配對資訊 | 科目配對 store + B 的 Not-in-TB | GL_NUMBER/GL_NAME/STANDARDIZED_ACCOUNT_NAME;在 GL 不在 TB 者 GL_NAME 寫字面「Not in TB」 |

**step4-1 的動態欄位**：只 emit「該客戶有行層命中」的 C*_TAG 欄。哪些欄要出，由 `tagMatrixScenarios` 中 `rowHitCount>0` 的 position 決定，以對齊樣本。標頭以位置寫成 `C{position}_TAG`。固定模板殘留的欄（樣本中的 Q–Z）不複製過來。

## 受影響的現行碼與新增（盤查）

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:`export.workpaperStream` stub→Implemented(形狀)、新 `host.selectSavePath`、facade、ActionContractTests |
| `Domain/GlCanonicalNames.cs`(新) | logical key → 正準中文名(GL+TB) |
| `Domain/WorkpaperContracts.cs`(新) | `WorkpaperContext`(專案 metadata + moneyScale + 期間 + 選表)、`ExportStats`(bytesWritten + sheetStats) |
| `Domain/I*CreatorSummaryExport*` + `Infrastructure/{Sqlite,SqlServer,ProviderRouting}*` | 不截斷全編製人員彙總查詢 |
| `Infrastructure/WorkpaperWriter.cs`(新)+ SAX 原語/sheet emitters | OpenXML SAX 串流寫出器(deep module) |
| `Application/ExportWorkpaperStreamHandler.cs`(新,取代 stub) | 編排:gather metadata、決定 sheets、呼叫 writer |
| `Bridge/Form1`(host) | `host.selectSavePath`(SaveFileDialog) |
| `AppCompositionRoot.cs` | 註冊 writer + handler + 查詢 + 正準名 |
| `wwwroot/js/jet-api.js` + `steps/export-step.js` + css | action 字串 + 匯出觸發/選表/完成回饋(零邏輯) |
| `docs/jet-guide.md`/`development-status.md`/`development-log.md`/`windows-handoff.md` | 回寫 + 待驗卡 |

## 新增／更新測試（TDD，三層）

- **Domain**：`GlCanonicalNames` 的每個 logical key 都有正準名，且與 `GlFieldWhitelist` 的鍵一致；以及匯出契約型別。
- **Application 驗收**（`HandlerTestHost` 加 demo）：呼叫 `export.workpaperStream` 寫到暫存檔，再用 reader 讀回斷言：（a）15 張（或經條件篩選後的張數）表都存在、表名正確；（b）封面 metadata 正確、step1 的欄標正確且至少一科目列的數值等於獨立重算、step1-2 的全名單筆數等於 distinct `created_by` 的重算、step2 有借貸兩欄、step3 的 C 數與 tagMatrixScenarios 一致、step4 的矩陣 Y 與 matchedPositions 一致、step4-1 的動態 C 欄集正確；（c）條件表：diff≠0 時 step1-3-1 存在、無差異時不存在（用兩個 demo 變體驗）；（d）手填欄為空白；（e）Not-in-TB 的字面值正確；（f）`sheetStats` 的列數正確。
- **Infrastructure/parity**：全編製人員查詢在 SQLite 與 SQL Server 兩側等價（`[SqlServerFact]`，可 clean skip）。
- **前端**：不寫 JS 業務測試；匯出觸發、選表、存檔、完成回饋列入 windows-handoff。
- 測試端讀回 `.xlsx` 可用既有的 OpenXmlSaxTableReader 或 ClosedXML（測試專案可用）斷言，不靠目視。

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需要本機 LocalDB 並設定 `JET_SQLSERVER_CONNECTION`（閘控）。

## Windows 端待驗任務（落地時寫入 windows-handoff.md）

「匯出底稿」步驟的完整流程是：選表（或全部）、觸發 `export.workpaperStream`、跳出存檔對話框（預設檔名 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`）、寫出、最後給完成回饋（bytesWritten 與各表列數）。驗收時要用 Excel 開啟，逐表目視對齊樣本：封面、step1 全科目、step1-2 全名單、step2 借貸兩欄、step3/4/4-1 的矩陣、step5 橫幅、三張參考表、以及 Not-in-TB 的字面值。還要確認條件表行為正確：diff≠0 的案件 step1-3-1 會出現、無差異的案件不出現。最後驗大母體（百創約 140 萬列）匯出時不爆記憶體、能跑完。

## 與其他子專案的邊界

- **D1/D2**：寫出器消費它們的查詢（逐頁串流），不改它們的語意。
- **B、C、C 補遺、F、A**：資料來源（Not-in-TB、授權清單衍生、行事曆、規則矩陣）都已落地；E1 純讀。
- **E2（延後）**：Field Mapping Info 的匯入 round-trip（讀回重建配對），會用 E1 的 `GlCanonicalNames` 做反向對照；E1 已把正準名表設計成雙向共用。
