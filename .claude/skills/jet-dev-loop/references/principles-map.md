# 原則對照地圖

這份地圖把通用的工程原則對應到本專案的具體落地方式與權威出處。它**不重述原則本身**,假設你已經懂 SOLID、Clean Code、SDD(Specification-Driven Development,規格驅動開發)、TDD(Test-Driven Development,測試驅動開發)。它只回答三個問題:這個原則在 JET 長什麼樣、規則寫在哪份文件、發生衝突時聽誰的。

## 目錄

- SOLID
- Clean Code
- Specification-Driven Development(契約先行)
- Test-Driven Development / ATDD / ISTQB
- 衝突仲裁順序

## SOLID

本專案那條「依賴方向不可違反」的鐵律,就是 SOLID 的具體化。權威在 `AGENTS.md` §Non-Negotiable Architecture:

- **依賴反轉(DIP)**:依賴方向是 `Bridge / Form1(Host) → Application → Domain ← Infrastructure`。Application 不得引用 Infrastructure。跨層共用的契約(`JetActionException`、`JetErrorCodes`、`JetJsonStorage`)一律放在 Domain。唯一一個有文件記載的例外是 `Infrastructure/DemoWorkbookWriter`,它實作了 Application 那個 dev-only 的 port `IDemoFileWriter`。
- **單一職責(SRP)**:每一層只做一件事。`Form1.cs` 是個 thin WebView2 host,不含業務邏輯;`Bridge/` 只負責 WebMessage 的 JSON 傳輸與 action 分派;`Application/` 擁有各個 use case;`Domain/` 是純規則、不依賴任何 framework;`Infrastructure/` 擁有檔案 I/O 與各 provider 專屬的 SQL。
- **開放封閉 / 里氏替換**:在同一個 Domain repository 介面之下,SQLite 與 SQL Server 是可互相替換的實作。provider 的分支只出現在 Infrastructure(`ProviderRouting*`),handler 只看得到介面、感知不到背後是哪個 provider(見 `docs/jet-guide.md` §13)。

落地時怎麼檢查:新增程式碼前,先確認它該落在哪一層、它的引用方向合不合法。檔案開始變大,通常是 SRP 已經失守的訊號。

## Clean Code

權威散見於 `AGENTS.md` 與既有的程式風格,重點如下:

- 命名與註解跟著周圍的程式碼走。註解只寫規格依據和不變量,不要寫流水帳。
- 斷言與邏輯要鎖在「可觀察的行為」上,不要鎖在實作細節上(見 jet-testing §3 的 fragile test)。
- 魔術數字要嘛自我說明,要嘛附上對應的 guide 條文出處。
- 文件寫作另有一份規範,在 `docs/README.md`:禁用流水代號、採描述性命名、標示狀態、領域名詞要精確。

## 工程品味準則

以下是跨子專案一貫採用的工程取捨基準,從「匯出底稿」這個里程碑沿用至今。這些準則在本專案沒有其他權威出處,所以就以本節為準:

- **資料結構優先(data-structure-first)**:先把資料結構與型別設計對,讓特例自然消失,而不是事後用一堆分支去補特例。
- **深模組、窄介面**:介面要小、實作要深。對外只露出必要的窄契約,把複雜度藏在實作裡(例如 `WorkpaperWriter` 對外只有 `WriteAsync`,回傳 `ExportStats`)。實作相對於介面要夠厚,這層抽象才划算。
- **DRY 門檻設在 3**:同一段邏輯要出現第三次,才抽成共用。前兩次先容忍重複,目的是避免過早抽象。
- **註解寫「為什麼」**:只寫規格依據、不變量、取捨的理由,不要複述程式碼在做什麼。
- **不碰無關的程式碼**:一次只改本任務範圍內的東西。順手看到的其他問題,記進待辦清單(ledger),不要夾帶進這次的變更。
- **複審分級**:回報問題時照 Critical、Important、Minor 三級分。品味類的問題也套同一把嚴重度標尺。

## Specification-Driven Development(契約先行)

這是本專案版本的「規格先於實作」。權威在 `AGENTS.md` §Contract-First Workflow 與 `docs/action-contract-manifest.md`,流程是:

1. 動 WebView2 bridge、workflow UX 或任何 backend action 之前,先讀 manifest。
2. 盡量重用既有的 action。
3. 確實需要新資料或新行為時,**先更新 manifest**,再實作。
4. 實作落地後,回頭把受影響的現況規格也改掉。規格與實作雙向同步,才能防止兩者漂移。

這對應到 jet-testing 的 ATDD(Acceptance Test-Driven Development,驗收測試驅動開發):Application 層的測試就是驗收測試,驗收條件來自 manifest 裡的 payload、response、error code。所以新增一個 action 的順序是:先改 manifest,再寫一個紅燈的驗收測試,最後實作 handler。

## Test-Driven Development / ATDD / ISTQB

這幾項的**唯一權威是 jet-testing skill**,本地圖不重述它的內容。jet-testing 涵蓋:測試金字塔與各測試該歸哪一層、FIRST 原則、被禁止的 test smells、設計技術(BVA 邊界值分析、等價分割、決策表、狀態轉換、pairwise)、oracle 策略、mutation 自查、風險排序,以及 prompt 模板。其中規則類的程式(Domain)優先採 test-first。

至於它們在開發迴圈裡的位置,見 SKILL.md 的階段 2(紅燈)與階段 3(綠燈)。

## 衝突仲裁順序

同一個主題在多處出現不同說法時,照下列優先順序裁決(由高到低):

1. 使用者的明確指示。
2. `docs/jet-guide.md`,它是領域與架構最深的權威。
3. 對應主題的現況規格:契約看 `action-contract-manifest.md`,測試看 jet-testing,架構邊界看 `AGENTS.md`。
4. 本 skill 與本地圖,它們只負責編排與指路。

本地圖只要和上述任一份權威衝突,就以權威為準,並把歧義回報出來。不要在本檔另立一份和權威打架的第二事實。
