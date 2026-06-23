# JET 開發現況

**自動化測試現況(2026-06-22):** 中止的 coverage 強化任務已收尾。保留下來的是有效覆蓋:金額與日期邊界、CSV/XLSX 讀取錯誤路徑、handler 錯誤契約、診斷日誌、SQL dialect、以及真實 provider repository 測試。移除掉的是無效填充:用 mock 設定回傳值再斷言同一值的自驗測試、違反專案邊界的 WinForms 自動化、以及永久 skip 的不可達分支。此處的測試數字是該輪的歷史紀錄;當前最新數字見下方「進行中」段。完整 SQLite + SQL Server LocalDB 套件當時為 **938 通過 / 0 失敗 / 0 略過**,production line coverage **95.29%**。

本檔是跨 session 的**現況快照**,記錄四件事:目前可用的功能、進行中的工作、規劃中的方向、已擱置的決策。每輪開發收尾時更新本檔。「為什麼變成這樣」的過程紀錄另寫在 `docs/development-log.md`。本檔不是契約來源——wire shape 一律查 `docs/action-contract-manifest.md`,業務語意一律查 `docs/jet-guide.md`。

## 最近幾輪開發

下列各段為一輪一段,新的在前。完整脈絡見 `docs/development-log.md`。

**2026-06-23 r2 — UX 修正七題 + 資料表三層正規化**。修正使用者回報的七個分散問題,並把現行散在 341 處 inline SQL 的表名收進中央登錄。重點包括:命名修正(分錄測試自動化工具)、建立案件移除產業別與金額 scale、非工作日週幾選擇器(預設週六日)、KCT 子小組獨立的 A–J 條件面板、進階篩選新增「考量特殊科目類別配對」條件、科目配對序位移到驗證步驟後段、新增 Domain `JetSchemaCatalog` 作為三層表名的唯一登錄。第七題刻意**不實體改名任何資料表**,只在呈現層用正規名,因此無 migration、無審計邏輯風險。全套件本機 **1087 綠**(0 failed、0 skipped,含 SQL Server LocalDB parity)。程式已完成、自動化全綠,GUI 目視驗收與版控提交都還沒做。

**2026-06-22 — 案件名稱資料夾命名 + 套用測試案件不跳步 + 授權編製人員預覽**。`project.create` 新增**選填**欄位 `caseName`:提供時即作為 `projectId` 與資料夾名(取代原本的雜湊,維持「資料夾名 == projectId == DB 鍵」不變式),未提供則回退 GUID,讓既有測試零改動。新增 Domain `ProjectNameRules` 作字元白名單驗證,它同時是 path-traversal 守衛,且是舊 32-hex GUID 的超集,因此既有專案零遷移。唯一性以同名資料夾即擋;SQL Server 另防「不同名稱淨化後撞同一庫名」。建立步的「套用測試案件」加 `stayOnCurrentStep`,套用後停在當前步、不自動跳匯入。`query.dataPreview` 新增 `authorizedPreparers` 資料集與匯入卡「預覽授權清單」鈕。全套件 **750 綠**(含 SQL Server parity),Opus 複審 READY-TO-MERGE、無 Critical。同日另做一輪安全升級:覆蓋 SQLitePCLRaw 至 3.0.3 修補 CVE-2025-6965。

**2026-06-21 — 測試案件資料擴充 + 配對介面優化**。配對介面切換鈕更名為「簡易清單/對照表格」(純顯示字串,內部值 classic/grid 不變);欄位配對「已提交」摘要卡改為唯讀二維對照表加樣本列預覽。核心是把 demo 測試案件重設計為 TDD 規則 oracle:`DemoDataFactory` 改成 7,000 張傳票、150 科目,分 baseline 與 seed 兩層——baseline 對每條規則貢獻 0 命中,每個 seed 群組貢獻一個已知數,因此每條規則的命中數就等於其 seed 常數。新增 `DemoRuleOracleTests` 端到端跑 prescreen.run/validate.run,斷言 13 條規則精確命中,再以獨立 set-based SQL 重算交叉驗證(雙 provider `[SqlServerFact]`)。全套件本機由 700 增至 **715 綠**(0 failed、0 skipped)。demo 資料全數去機敏:公司名固定為虛構常數「範例製造股份有限公司」,人名與摘要皆虛構。待 GUI 人工驗收。

**2026-06-21 — 子專案 E1(匯出底稿 writer)**。這是「匯出底稿」里程碑的最後一棒,把 `export.*` 從委派前端的 stub 換成真正的後端串流寫檔。新增 Infrastructure `WorkpaperWriter`,以 OpenXML SAX(`OpenXmlWriter` inline string)串流逐頁寫出 15 張(其中一張條件性)工作表的 `.xlsx`(檔名 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`)。writer 是 deep module:對外只暴露窄介面 `WriteAsync(Stream, context) → ExportStats`,內部藏全部 OpenXML 細節;資料表共用單一 `EmitTableSheet` 原語,逐頁 keyset 取資料逐列寫,不把整份結果載入記憶體、也不重算任何規則。它是查詢消費者,注入既有的 D1/D2 查詢 repo 與 store。新增三類後端查詢(皆 `[SqlServerFact]` parity):全編製人員彙總、全科目完整性頁、AccountMapping/Calendar 匯出。新增 Domain `GlCanonicalNames`(logical key 對正準中文名)、`export.workpaperStream` action(取代 stub)、`host.selectSavePath`(WinForms `SaveFileDialog`)。手填欄寫空白骨架;封面 CAATs 段只寫檔名字串、不產生 docx。round-trip 匯入延後到 E2(使用者裁定)。全套件本機 **700 綠**,待 GUI 人工驗收。

**2026-06-21 — 子專案 D2(多情境逐列 tag 矩陣)**。新增三個查詢 action,即時從 `result_filter_run` pivot 出方法學 step4(傳票層命中矩陣)與 step4-1(行層逐行 tag)所需的資料,不另存矩陣表。`query.tagMatrixScenarios` 回所有已存情境的命中數(0 命中也列);`query.tagMatrixVoucherPage` 回去重的命中傳票;`query.tagMatrixRowPage` 回命中傳票的所有行(含非命中行)。每頁拆兩段查詢——實體頁 keyset 一段、同鍵範圍的位置一段,在 handler 內分組,以避開 `group_concat`/`STRING_AGG` 這類方言相異的聚合。新增 `idx_result_filter_run_entry` 索引(不升 schema 版本)。把 `filterHitsPage` 的惰性補算提取成共用服務 `FilterRunMaterializeService`。全套件本機 **646 綠**,待 GUI 人工驗收。

**2026-06-21 — 子專案 C 補遺(低頻科目)**。補上 RowTag 規則 `low_frequency_account`(R12):命中全期分錄筆數 ≤ 固定常數 `AccountFrequency.DefaultMaxEntries=11` 之 `account_code` 的所有分錄。它的述詞自足、不依賴外部清單,因此沒有 `NOT IN` 空集合反轉成全命中的風險。它與既有 `rare_accounts`(R6,top-50 彙總、不可作列述詞)並存不取代。另加進階篩選條件 `customAccountEntryCount`,讓查核員自訂門檻取代固定的 11。整體鏡射子專案 C 的 `low_frequency_preparer`/`customPreparerEntryCount` 雙軌,無新 schema 表或欄位。全套件本機 **628 綠**,待 GUI 人工驗收。

**2026-06-20 — 子專案 C(編製人員升級)**。新增授權編製人員清單匯入 `import.authorizedPreparer.fromFile`:讀單欄姓名 .xlsx、replace-only、TRIM 去重、不寫 import_batch、兩張新表以 IF NOT EXISTS 建立不升 schema 版本,replace 時於同交易使依賴名單的規則結果失效。新增兩條 RowTag 預篩選規則:非授權編製人員(`non_authorized_preparer`,R10,`created_by` 不在授權名單)與低頻編製者(`low_frequency_preparer`,R11,全期編製筆數 ≤ 固定常數 11)。R10 採雙重閘控——預篩選端若名單為空整條回 na,filter 端 validator 對空名單回 `invalid_scenario` 且述詞以 `EXISTS` 自保,以免 `NOT IN` 空集合反轉成全命中。另加自訂門檻條件 `customPreparerEntryCount`。不動既有 `creator_summary`。全套件本機 **618 綠**,待 GUI 人工驗收。

**2026-06-20 — 子專案 D1(全量明細基礎設施)**。新增五個 keyset 游標分頁 action(`query.completenessDiffPage`/`docBalancePage`/`nullRecordsPage`/`filterHitsPage`/`infSamplePage`),請求帶 `{cursor,pageSize}`、回應帶 `{rows,nextCursor}`,pageSize 預設 200、上限 500。游標述詞寫成展開布林式,以便 SQLite 與 SQL Server 共用同一邏輯(SQL Server 不支援元組比較)。新增 `result_filter_run` 表(不升 schema 版本)落地已存篩選情境的命中 entry_id,並納入結果失效集。前端在五處明細預覽下方加「載入更多」(零商業邏輯)。全套件 **588 綠**(含 8 個 ProviderParityJourney passed),GUI「載入更多」待 Windows 端驗收。

**2026-06-20 — 子專案 B(資料/schema 擴充)**。新增選填傳票日期欄 `voucher_date`(schema v4→v5 守欄遷移,含民國年正規化)。新增回溯過帳預篩選規則 `backdated_posting`(R9,命中過帳日早於傳票日的傳票)。完整性測試新增 part(a) 控制總數核對(來源端 vs 投影母體的列數與借貸總額),並為差異列加 Not-in-TB 具名記號。新增 `gl_control_total` 表並納入結果失效集。全套件 **559 綠**,GUI 待 Windows 端驗收。

**2026-06-20 — 子專案 A(規則校正)**。連續零尾數改為固定預設 6 位,取代原本的動態門檻;統一 off-by-one 的文件口徑;可疑關鍵字清單補上「帳外」;高風險情境上限由 5 調為 10。授權閘以「自訂尾數位數 AND 數值區間」的進階篩選情境組合表達,並於 guide §5 文件化。

更早的兩件大事:SQL Server provider 的全 repo 移植與診斷日誌 instrumentation 的全 repo 接入,皆於 2026-06-14 完成,過程見 `docs/development-log.md`。

## 目前可用的功能

六步驟審計工作流程,前五步已具備真實後端;第六步(匯出底稿)後端 writer 已實作,待 GUI 驗收:

1. **建立案件**:專案以 `projects/{projectId}/` 資料夾持久化(`project.json` 中繼資料 + `jet.db` SQLite 資料庫,schema 第 5 版);啟動時的專案選擇畫面支援載入續作(resume)。
2. **匯入資料**:`.xlsx` / `.csv` / `.txt` 串流匯入暫存表(payload 只傳檔案路徑,不搬資料列)。**xlsx 走 OpenXML SAX 串流讀取**(單一讀取器、無檔案大小分支;檢視與讀欄名讀完標頭即返回——114MB 真實 PBC 檔 inspect 實測 203ms),**百萬列等級實證**:真實 PBC 活頁簿(兩工作表合計 1,403,327 列)匯入+投影全程約 53 秒(SQLite WAL + 匯入連線 pragmas;閘控測試 `PbcRealDataSmokeTests` 以 `JET_PBC_DIR` 環境變數重現)。**多來源匯入精靈**:一個 GL/TB 資料集可由多個檔案或多個工作表合併組成;批次欄位採**有效欄位收斂**(guide §3.1.5:具名標頭一律保留、`COL_n` 佔位欄僅在有資料時成立——解開真實檔「上半年空欄佔位 vs 下半年連續欄」的合併阻斷),附加驗證兩階段(具名快檢 + 串流後終檢);工作表清單顯示推估列數(`rowCountEstimate`),匯入期間以 `import.progress` 事件(每 20,000 列)顯示進度條。文字檔有編碼偵測鏈(BOM → 嚴格 UTF-8 驗證 → Big5)與引號感知的分隔符偵測;金額接受會計格式零(單獨 `-` 視為 0,guide §3.1.2)。**科目配對匯入**(`import.accountMapping.fromFile`):固定三欄格式(科目代號/名稱/標準化分類白名單,標頭吃英文欄名對齊事務所實際檔案)、匯入即投影、replace-only;解鎖未預期借貸組合與科目配對分析。**行事曆檔案匯入**(`import.holiday.fromFile` / `import.makeupDay.fromFile`):事務所行事曆 .xlsx(schema v4、標頭第 2 列;假日檔 `Date_of_Holiday`／`Holiday_Name`／`IS_Holiday`,只收 IS_Holiday=Y、跨年度全收;補班檔 `Date_of_MakeUpday`／`MakeUpDay_Desc`),「匯入資料」步驟「日期維度」卡兩顆按鈕(上傳假日／補班)走 `hostSelectFile → import*FromFile`,假日與補班互不歸零;另保留 demo 管線餵入路徑。**授權編製人員清單匯入**(`import.authorizedPreparer.fromFile`,子專案 C):查核團隊維護的單欄姓名 .xlsx(英文標頭),沿用 OpenXML SAX 讀取與關鍵字欄位解析;TRIM 正規化、空白列略過、去重存入 `target_authorized_preparer`;replace-only、不寫 `import_batch`;重匯入時依賴名單的非授權編製人員規則結果在同交易失效。「匯入資料」步驟有授權清單上傳卡;`project.load` 的 importState 回 `authorizedPreparer.rowCount` 供 resume。重新匯入或加入來源完成後,匯入卡片會短暫顯示一個成功態（「剛剛重新匯入」／「已加入來源」加上完成時間）再淡回常態摘要,讓使用者一眼確認剛才真的重跑了。
3. **欄位配對**:後端自動建議 + 提交標準化(GL 四種金額模式、TB 兩種變動模式;金額轉 scaled BIGINT、日期正規化含民國年支援)。GL 可配對欄含選填**傳票日期**(`voucherDate → voucher_date`,日期正規化同其他日期欄,自動建議同義字;來源未配對則為 NULL),供回溯過帳規則與進階篩選日期區間使用。前端為五態狀態模型:已提交收合為摘要卡(僅「重新配對」「預覽標準化資料」),草稿偏離與來源變更失效各有說明橫幅。草稿態的編輯介面改為內嵌標頭下拉的二維配對表:來源欄為直行、標頭下拉指派 JET 欄位,標頭下方鋪該批次前 10 列原貌(重用 `query.dataPreview`),提交契約不變。草稿態現在提供簡易清單與對照表格兩種介面,由頂部切換鈕選定(預設簡易清單、本次 session 記住);對照表格加了右側必填鐵軌(必填指派後翻綠)與加寬版面,提交契約一樣不變。已提交摘要卡改以唯讀對照表呈現配對(來源欄為表頭、其下標對應的 JET 欄位、附該批次前 10 列樣本,side/flag 模式另補借方代碼字面值),取代原 key→欄名 pill 清單,讓審計員一眼看出哪欄對哪欄、實際長什麼樣。
4. **資料驗證與測試**:四項資料驗證(完整性測試/借貸不平測試/INF 抽樣測試/空值紀錄測試)與十三項預篩選(期末後核准、摘要特定描述、未預期借貸組合、連續零尾數、編製者彙總、罕用科目、週末過帳/核准、假日過帳/核准、回溯過帳、非授權編製人員、低頻編製者、低頻科目、摘要空白),全部以參數化集合式 SQL 執行;結果摘要落地 `result_rule_run` 供載入回放;INF 抽樣可重現(seed 落地)。**非授權編製人員**(`non_authorized_preparer`,R10)以述詞 `created_by NOT IN (授權清單)` 命中(空白 created_by 不命中),屬 RowTag、可作進階篩選列述詞;**雙重閘控**——預篩選端以 `HasAuthorizedPreparers`(名單表非空)放行、未匯入則整條 `na`,filter 端 validator 對空名單回 `invalid_scenario` 且述詞另以 `EXISTS` 自保(避免 `NOT IN` 空集合反轉為全命中);存成情境後以 D1 `filterHitsPage` 取回該非授權人員的全部分錄(escalation,不另造基礎設施)。**低頻編製者**(`low_frequency_preparer`,R11)命中全期編製筆數 ≤ 固定常數 `PreparerFrequency.DefaultMaxEntries=11` 之 `created_by` 的所有分錄,屬 RowTag;門檻另有進階篩選條件 `customPreparerEntryCount`(自訂 `maxEntries ≥ 1`)可取代固定 11。**低頻科目**(`low_frequency_account`,R12)命中全期分錄筆數 ≤ 固定常數 `AccountFrequency.DefaultMaxEntries=11` 之 `account_code` 的所有分錄,屬 RowTag(述詞自足無外部清單、無 `NOT IN` 反轉風險);與既有 `rare_accounts`(R6 top-50 彙總、不可作列述詞)並存不取代;門檻另有進階篩選條件 `customAccountEntryCount`(自訂 `maxEntries ≥ 1`)可取代固定 11;存成情境後以 D1 `filterHitsPage` 取回全部分錄(escalation,不另造基礎設施)。**回溯過帳**(`backdated_posting`,R9)以述詞 `voucher_date IS NOT NULL AND post_date < voucher_date` 命中過帳日早於傳票日的傳票(傳票日空白不命中),屬 RowTag,可作進階篩選列述詞。**完整性測試**除既有 part(b) 逐科目 TB 變動 vs GL 差異外,新增 **part(a) 控制總數核對**(投影時落地 `gl_control_total`:來源端列數/金額 vs 投影母體列數/借貸總額 scaled,核對列數與金額是否一致),part(b) 差異列附 **Not-in-TB** 具名記號(GL 有、TB 無)。**規則一律用 guide §4 命名登錄表的具體名稱,V/R/A 代號已退役**(wire key = lowerCamelCase、資料表 slug = snake_case、UI/底稿 = 中文名;程式內單一事實來源 `Domain/RuleCatalog.cs`)。**全量明細分頁**:完整性差異、借貸不平、空值紀錄、INF 抽樣四項驗證的明細除 ≤50 預覽外,均可經 keyset 游標 action(`query.completenessDiffPage`／`docBalancePage`／`nullRecordsPage`／`infSamplePage`)走訪全量,GUI 在預覽下方提供「載入更多」(游標增量、零商業邏輯)。**INF 60 行明細回取**(`infSamplePage`)讀既有抽樣 entry_id join 母體、限定最新 validate run、借貸由 signed amount 拆兩欄,只讀回不重抽樣。
5. **進階條件篩選**:前端 Query Builder 組條件 AST → 後端轉參數化 SQL 預覽(≤50 列)與情境保存(≤10 個;事務所底稿高風險條件 C1–C10)。條件型別十二種:預篩選 row-tag、文字、數值區間、日期區間、期內/期外、借貸限定、人工/自動、自訂關鍵字、自訂尾數位數(1–12)、自訂編製人員張數(`customPreparerEntryCount`,自訂 `maxEntries ≥ 1`,同低頻編製者述詞)、自訂科目張數(`customAccountEntryCount`,自訂 `maxEntries ≥ 1`,同低頻科目述詞)、科目配對分析(精確/借方錨定/貸方錨定三模式;需科目配對已匯入)。0 元金額統一屬借方側(`>= 0`,2026-06-11 裁決)。授權閘以「自訂尾數位數 AND 數值區間」情境組合表達,語意見 guide §5。**已存情境命中落地**:`filter.commit` 保存情境時以 `GlFilterWhereBuilder` 把命中 `entry_id` 寫入新表 `result_filter_run`(行層參照、先刪後插冪等);`query.filterHitsPage`(帶 `scenarioPosition`)keyset 讀回該情境全量命中明細,對舊情境惰性補算;GUI 情境命中明細提供「載入更多」。**多情境 tag 矩陣**(子專案 D2):三個 query action 即時從 `result_filter_run` pivot 出方法學 step4(傳票層 C1..CN 命中布林矩陣)與 step4-1(行層逐行 tag)的矩陣資料——`query.tagMatrixScenarios`(情境表頭 + 傳票/行命中數)、`query.tagMatrixVoucherPage`(去重命中傳票 + `voucherTotal` + `matchedPositions`,keyset `document_number` ASC)、`query.tagMatrixRowPage`(命中傳票之所有行 + 逐行 `matchedPositions`,keyset `entry_id` ASC);不落地新矩陣表,每頁兩段查詢(實體 keyset + 同鍵範圍位置,handler 分組,避方言聚合),新增 `idx_result_filter_run_entry(entry_id, scenario_position)` 索引(不升版),惰性 materialize 經共用 `FilterRunMaterializeService`;前端「高風險條件矩陣」預覽(情境摘要 + 傳票矩陣 + 點傳票展開行層 tag,零邏輯)。矩陣餵 E(writer),D2 只提供查詢。
6. **匯出底稿**:後端 writer 已實作,待 GUI 驗收(子專案 E1)。`export.workpaperStream`(取代原 stub)經 Application handler 編排、由 Infrastructure `WorkpaperWriter` 以 OpenXML SAX(`OpenXmlWriter` inline string)**串流逐頁寫出** 15(條件)工作表的 `.xlsx`(`{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`):封面 ×2、step1 完整性(全科目)、step1-1 借貸不平、step1-2 分錄編製人員說明、step1-3 完整性差異說明、step1-3-1 完整性差異調節(條件,diff≠0 才出)、step2 可靠性(借/貸兩欄)、step3 高風險彙總、step4 符合條件傳票、step4-1 傳票明細(動態 `C{position}_TAG` 欄)、step5 關帳後調整,加三張參考表(檔案欄位資訊、假期假日資訊、科目配對資訊含 Not-in-TB 字面值)。writer 是 deep module(窄介面 `WriteAsync→ExportStats` 藏 SAX 細節、單一 `EmitTableSheet` 原語、條件表為 orchestration guard)、是查詢消費者(注入 D1/D2 查詢 repo + store,keyset 逐頁取逐列寫,**不全載入記憶體、不重算規則**);**ClosedXML 僅 dev fixture**。手填欄寫空白骨架;封面 CAATs 段只寫檔名字串、不產生 docx。`host.selectSavePath`(WinForms `SaveFileDialog`)提供存檔路徑;前端「匯出底稿」步驟接線(選表 + 觸發 + 完成回饋,零商業邏輯)。**round-trip 匯入延後 E2**。逐表 Excel 目視對齊樣本、大母體匯出不爆記憶體等 GUI 互動仍待人工驗收。

支撐機制如下。每一步都有「套用測試案件」按鈕,走的是和使用者上傳完全相同的 file-based 管線(deterministic demo fixture,含科目配對)。步驟導航有閘門模型。使用者所在的步驟會即時保存,因此「儲存並結束」與重開續作都可用。資料預覽面板(正式版功能,`query.dataPreview`)對六個資料集(GL/TB 來源原貌、GL/TB 標準化後、科目配對、授權編製人員清單)提供有界預覽(≤50 列加總列數;GL 測試母體另附概況統計)。開發者面板僅在 Debug 組建註冊與顯示。

診斷日誌(第三層、dev-only)除了記憶體 ring buffer 與 DEV 面板 `dev.log.export` 匯出之外,另以檔案 sink `NdjsonFileLoggerProvider` 即時寫入 `%LOCALAPPDATA%\JET\logs\jet-dev-*.ndjson`。如此一來,agent 跑完 app 後可直接讀檔取得執行時真相。Release 組建下整條 no-op。

**Provider 策略**:SQL 方言相異片段集中於 `ISqlDialect`(含 keyset 分頁的 `LimitClause`:SQLite `LIMIT`、SQL Server `OFFSET 0 ROWS FETCH NEXT`),規則述詞單一事實來源 `GlRulePredicates` 與 WHERE 組譯 `GlFilterWhereBuilder` 皆 provider 中立(DbCommand)。keyset 游標述詞一律展開布林式(`key > @cursor`),不用元組比較(SQL Server T-SQL 不支援),兩 provider 共用。**SQLite 與 SQL Server 皆已實作**(2026-06-14 完成全 repo 移植與 provider 路由,雙 provider 端到端等價;本機 LocalDB 閘控測試實跑)。等價測試抽象基底(手算 fixture、命中傳票集合斷言)讓 DuckDB 等未來 provider 只需新增子類即可跑同一套。

**Schema 遷移**:第 1/2/3/4 版資料庫開啟時自動鏈式升級至第 5 版(冪等)。v2→v3 的相容策略:舊鍵規則執行摘要清除(重跑即恢復且結果相同——抽樣 seed 固定;升版後 `latestRuns` 為 null,前端顯示「未執行」),篩選情境的 `prescreenKey` 逐鍵翻譯保留。v3→v4 為 `staging_calendar_raw_day` 加假日/補班名稱欄 `day_name`(雙 provider,ALTER 前守欄、冪等)。v4→v5 為 `target_gl_entry` 加選填傳票日期欄 `voucher_date`(`ALTER TABLE … ADD COLUMN`,ALTER 前以 `pragma_table_info`/`INFORMATION_SCHEMA.COLUMNS` 守欄、冪等),並新增控制總數表 `gl_control_total`;既有 `result_rule_run` 回放不受影響。`result_filter_run`(已存情境命中的行層落地)隨基底 schema 以 `IF NOT EXISTS`／`IF OBJECT_ID(...) IS NULL` 建立,**不升 schema 版本**(同 `app_message_log`／`gl_control_total` 先例),並納入結果失效集(重投影清 `target_gl_entry` 時同交易 `DELETE`)。子專案 C 的授權編製人員兩新表 `staging_authorized_preparer_raw_row` 與 `target_authorized_preparer(name PRIMARY KEY)` 同樣以 `IF NOT EXISTS`／`IF OBJECT_ID(...) IS NULL` 隨基底 schema 建立、**不升 schema 版本**;授權清單匯入為 replace-only、**不寫 `import_batch`**(非 GL/TB 母體匯入),replace 時於同交易 `ClearWithinAsync` 使依賴名單的非授權編製人員規則結果失效。

橋接契約:`JetApi` 方法以 manifest 的「JetApi Typed Facade」章節為權威,該章節列出完整清單與數量。各輪對 facade 的增補為:D1 加 5 個 `query.*Page`、D2 加 3 個 `query.tagMatrix*`、E1 把 `export.workpaperStream` 由 stub 換成正式 facade 並新增 `host.selectSavePath`,而 `dev.db.*` 僅在 Debug 組建註冊。

除了 request/response,另有一條 host→web 事件管道(信封 `{ event, data }`、以 `JetApi.on/off` 訂閱,首個事件是 `import.progress`):Application 經 `IJetEventPublisher` port 發出,Bridge 的 `WebViewEventPublisher` 負責 marshal 回 UI 執行緒。「狀態與訊息」面板預設收合為右側窄欄(帶未讀徽章);訊息持久化於專案資料庫(`log.append`/`log.recent`,每專案留最近 500 則),載入時還原。前端 JavaScript 為模組化結構:`ui-core` 共用核心、每步驟一個渲染模組、加殼層。

測試共 700 個,本機全數通過(0 failed、0 skipped)。涵蓋範圍依主題分述如下:

- **匯入與讀取**:SAX 讀取器行為移植與 raw-zip 邊角、日期格式判定、欄位收斂、會計零、WAL、規模煙霧、進度事件、訊息持久化、行事曆檔案匯入。
- **規則與 schema**:回溯過帳規則、完整性 part(a) 與 Not-in-TB、schema v4→v5 遷移冪等。
- **D1 分頁**:五個 keyset 分頁 action 走訪到底結果等於 recount、回應契約鎖、`PageCursor` 編解碼與頁大小夾擠、`result_filter_run` 命中與失效、雙 provider 逐頁等價(8 個 ProviderParityJourney 實跑)。
- **編製人員與科目規則**:授權清單匯入(replace-only、TRIM、去重、失效);非授權編製人員規則(雙重閘控、空名單回 na、EXISTS 自保);低頻編製者與低頻科目規則(固定門檻 11 的命中等於 recount,入情境後經 `filterHitsPage` 取回全部);`customPreparerEntryCount` 與 `customAccountEntryCount` 自訂門檻;`FilterableKeys == RowTag 集合`不變量(含 R12)。
- **D2 矩陣**:矩陣列型別欄序對齊 wire;三個 query action 對 manifest wire 驗收——scenarios 的命中數等於獨立 recount、0 命中情境也列出、惰性 materialize;voucherPage 的命中傳票集與 matchedPositions 與 voucherTotal 各自對 recount、走訪無重複單一升冪、壞 cursor 回 invalid_payload;rowPage 的列集等於命中傳票所有行(含非命中行)recount、每行 matchedPositions 對 recount、走訪等價;三查詢雙 provider 走訪等價。
- **真實 PBC 閘控全程**:含 1,403,327 列的逐列金額保真差分驗算——獨立 decimal 重算與 DB scaled SUM 位元相等、量化事件 0 件。
- **E1 匯出 writer 三層**:Domain 驗 `GlCanonicalNames` 全 logical key 有正準名且與 `GlFieldWhitelist` 鍵一致、匯出契約型別;Application 把 `export.workpaperStream` 寫到暫存檔後讀回斷言(15 張(條件後)表存在且表名正確、封面 metadata、step1 欄標與至少一科目列值對 recount、step1-2 全名單對 distinct created_by recount、step2 借貸兩欄、step3 的 C 數與 `tagMatrixScenarios` 一致、step4 矩陣 Y 與 matchedPositions 一致、step4-1 動態 C 欄集正確、step1-3-1 在 diff≠0 時存在/無差異時不存在、手填欄空白、Not-in-TB 字面值、`sheetStats` 列數正確);Infrastructure parity 驗全編製人員與全科目完整性查詢 SQLite 對 SQL Server 等價。

## 驗證指令與平台分工

```bash
dotnet restore src/JET/JET.slnx
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

macOS 開發端只能做到 build(目標框架 `net10.0-windows`,缺 WindowsDesktop runtime 無法執行測試);測試執行與 WinForms/WebView2 手動驗證須在 Windows 端完成,任務以 `docs/windows-handoff.md` 的任務卡交接。本輪(2026-06-11)開發與測試皆在 Windows 端完成,GUI 目視項見交接文件。

## 進行中:本輪成果的 GUI 人工驗證

所有列在下面的輪次都已完成程式實作、自動化測試全綠,但 GUI 目視驗收與版控提交都還沒做。GUI 互動依測試硬邊界(禁止 WinForms/WebView2 E2E 自動化)一律以 `docs/windows-handoff.md` 的任務卡交人工。各段的測試數字是該輪收尾時的全套件結果;最新一輪是 2026-06-23 r2 的 1087 綠。

**2026-06-23 r2 — UX 修正七題 + 資料表三層正規化**(設計提案 `docs/specs/2026-06-23-jet-ux-and-schema-canonicalization-design.md`)。七題全部實作並經多代理 SDD 複審 APPROVED,全套件本機 **1087 綠**(0 failed、0 skipped,含 SQL Server LocalDB parity)。七題為:① 命名修正(分錄測試自動化工具)② 建立案件移除產業別與金額 scale ③ 非工作日週幾選擇器(預設週六日)④ KCT 子小組獨立的 A–J 面板,後端豁免該情境的名稱與動機必填 ⑤ 進階篩選新增「考量特殊科目類別配對」條件 ⑥ 科目配對序位移到驗證步驟後段,驗證跑過即解鎖 ⑦ 中央三層表名登錄 `JetSchemaCatalog` 加資料預覽正規目錄。第七題刻意不實體改名,因此無 migration、零審計邏輯風險。

**KCT Phase 1**(九條 A/C/D/H/J 新型別加 E/F/G/I 預設)亦待併同驗收。它的 Step 4 呈現已於 r2 改為獨立顯著的 A–J 面板,取代原本的「快速加入第 5 組」。KCT 的 **B**(借固定資產貸費用)仍列 Phase 2,要等 BS/IS 分類維度到位。

**子專案 E1(匯出底稿 writer)**。`WorkpaperWriter` 以 OpenXML SAX 串流寫出 15 張(其中一張條件性)工作表,加三類新後端查詢(全編製人員彙總、全科目完整性頁、AccountMapping/Calendar 匯出,皆 `[SqlServerFact]` parity)、`GlCanonicalNames` 正準名表、`export.workpaperStream`(取代 stub)、`host.selectSavePath`,以及前端「匯出底稿」步驟接線。自動化以 reader 讀回暫存 `.xlsx` 斷言(表存在/表名、封面 metadata、step1 欄標與科目列值對 recount、step1-2 全名單對 distinct created_by recount、step2 借貸兩欄、step3/4/4-1 矩陣與 `tagMatrix*` 一致、條件表在 diff≠0 時存在/無差異時不存在、手填欄空、Not-in-TB 字面值、`sheetStats` 列數)。全套件本機 700 綠。待人工驗收的 GUI 與端到端互動:選表並觸發 `export.workpaperStream`、存檔對話框帶預設檔名、寫出後完成回饋、用 Excel 逐表目視對齊樣本、大母體(約 140 萬列)匯出不爆記憶體。round-trip 匯入延後 E2。

**子專案 D2(多情境逐列 tag 矩陣)**。三個查詢 action `query.tagMatrix{Scenarios,VoucherPage,RowPage}`、`idx_result_filter_run_entry` 索引、共用的 `FilterRunMaterializeService`,加前端「高風險條件矩陣」預覽。Application 對 manifest wire 驗收各情境命中數對獨立 recount、matchedPositions 對 recount、voucherTotal 對 SUM(debit) recount、走訪無重複單一升冪、壞 cursor 回 invalid_payload;Infrastructure 驗三查詢雙 provider 走訪等價。全套件本機 646 綠。待人工驗收:情境摘要、傳票矩陣載入更多、點傳票展開行層 tag、存滿 10 情境的欄位對位、大母體逐頁流暢、未存情境的友善空狀態。

**子專案 C 補遺(低頻科目)**。R12 `low_frequency_account` 規則、`customAccountEntryCount` 自訂條件、前端預篩選一條加自訂條件 UI。自動化驗 `AccountFrequency.DefaultMaxEntries==11`、`FilterableKeys` 含 R12 的不變量、固定 row-tag 與自訂門檻命中對 recount、入情境經 `filterHitsPage` 取回全部,雙 provider 等價。全套件本機 628 綠。待人工驗收:預篩選「低頻科目之分錄」的檢視加載入更多、進階篩選自訂科目張數條件、存情境後分頁看全部分錄。

**子專案 C(編製人員升級)**。授權清單匯入、非授權編製人員 R10、低頻編製者 R11、`customPreparerEntryCount` 自訂條件,加前端匯入卡與兩規則與自訂條件 UI。自動化驗清單解析、門檻常數、述詞語意、匯入 replace-only 與失效、兩規則命中對 recount、R10 雙重閘控與空名單回 na、自訂門檻,雙 provider 等價。全套件本機 618 綠。待人工驗收:授權清單上傳卡、兩規則的檢視加載入更多、進階篩選自訂編製人員張數條件、存非授權情境後分頁看全部分錄。

**子專案 D1(全量明細基礎設施)**。五個 keyset 分頁 action、`result_filter_run` 落地、INF 明細回取、前端五處「載入更多」。自動化驗 `PageCursor` 編解碼與頁大小夾擠、各 Page 走訪到底對 recount 與回應契約鎖、雙 provider 逐頁等價(含 8 個 ProviderParityJourney 實跑)。全套件 588 綠。待人工驗收:五處「載入更多」的游標增量接列、到底隱藏、載入中不卡頓。

**子專案 B(資料/schema 擴充)**。傳票日期欄、回溯過帳規則、完整性 part(a) 控制總數、Not-in-TB 具名化。自動化涵蓋 Domain 單元、Application 驗收、Infrastructure parity,含 SQL Server LocalDB 閘控。待人工驗收:欄位配對出現選填傳票日期、預篩選多出回溯過帳、完整性顯示 part(a) 與 Not-in-TB 標示。

更早幾輪的待驗收項也仍未做完。PBC 匯入強健化(SAX 串流讀取、欄位收斂、會計零、進度推播)的 GUI 互動——精靈進度條動態、估計列數顯示、實機耗時與記憶體記錄——仍待人工;前幾輪累積的手動 GUI 卡同樣仍待人工。

## 規劃中(尚未排程)

- **KCT 進階篩選 Phase 2:BS/IS 科目分類維度 + 條件 B**:條件 B(借固定資產 PPE/不含在建、貸費用)需要目前沒有的科目分類。已定方案——**新增獨立的 BS/IS 分類匯入**(新檔/新表,與科目配對表分離);類別字彙待 **KCT 交付完整 BS·IS 科目分類清單**後依表設計白名單與匯入格式。清單到位即另起一輪,屆時補完整設計並回寫 guide §5/§6 與本檔。設計脈絡見 `docs/specs/2026-06-23-kct-advanced-filter-conditions-design.md`。
- **匯出底稿里程碑(剩餘部分)**:全量明細基礎設施(D1:`query.*Page` keyset 分頁、`result_filter_run` 落地、INF 明細回取)、多情境逐列 tag 矩陣查詢(D2:`query.tagMatrix*` 三 action、即時 pivot 不落地、`idx_result_filter_run_entry` 索引、共用 materialize 服務)與**匯出 writer(E1:`WorkpaperWriter` OpenXML SAX 串流寫檔、`GlCanonicalNames` 正準名對照、step4/step4-1 動態 `C{position}_TAG` 欄、`export.workpaperStream`/`host.selectSavePath` 正式化)皆已落地(待 GUI 驗收)**;工作底稿分頁名用命名登錄表的中文規則名。**尚待 E2:欄位配對匯入 round-trip**(讀回 Field Mapping Info 重建配對,反向用 E1 已備妥的 `GlCanonicalNames`)。
- **假日名稱契約擴充**:假日過帳/核准底稿需要假日名稱,先修 manifest 再實作。
- **DuckDB provider**:本機分析引擎選項;同上,新增 `DuckDb*` 家族與 dialect 即可,等實際需求(如本機效能瓶頸)出現再立里程碑。

## 已知技術債與已擱置決策

| 事項 | 現況與處理時機 |
|:---|:---|
| CancellationToken 尚未串接 | bridge 一律傳 `CancellationToken.None`;百萬列匯入(實測單表約 20 秒)期間不可取消,待長任務(匯出)實作時補 per-request 取消 |
| 投影(mapping.commit)階段無進度推播 | 投影串流在倉儲內部(`SqliteGl/TbRepository`),加進度需在 Domain 倉儲契約加 progress 參數;實測 140 萬列投影 14.2 秒,暫可接受,匯出里程碑一併評估 |
| `dev.db.overview` 的 wire 欄位名 `sqliteVersion` 綁定 provider | Domain 已是中性的 `EngineVersion`;下次 dev.db 契約變更時改名 `engineVersion`(manifest 先行) |
| CSV 讀取器重複開檔 | 讀欄名與讀資料列各自開檔重新偵測編碼/分隔符(xlsx 讀欄名為標頭即停的串流,成本可忽略);prototype 規模可接受,匯入效能有感時再做快取 |
| staging `row_json` 的磁碟佔用 | 140 萬列約 1GB 資料庫檔 + WAL 暫態峰值;本機案件可接受,超大母體屬 SQL Server 路線 |
| `projects/` 根目錄位置 | 目前在 `AppContext.BaseDirectory` 下;正式安裝到唯讀位置前需改 `%LOCALAPPDATA%`(composition root 一行) |
| 科目配對分析的精確模式與借貸錨定共用一組分類下拉 | 錨定模式只用單側分類,另一側下拉仍顯示(後端只驗必填側);前端依模式動態隱藏屬視覺優化,待下輪 UI 微調 |
| 完整性 part(a) 控制總數在完整管線後恆 `na` | `gl_control_total` 僅在 GL 投影時寫入,TB 投影的 `RuleRunResultReset` 把它清掉後不重建(失效集當初未區分 GL/TB 相關變更),故「匯入→配對→驗證」完整跑完後,匯出底稿 step1 part(a) 控制總數讀為 `na`;part(b) 逐科目差異不受影響。待匯出底稿里程碑收尾、或失效集改為區分 GL/TB 變更時一併修 |
