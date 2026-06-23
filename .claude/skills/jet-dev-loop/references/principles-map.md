# 原則對照地圖

把通用工程原則對應到本專案的具體落地與權威出處。**不重述原則本身**(假設已懂 SOLID/Clean Code/SDD/TDD),只說「在 JET 它長什麼樣、規則寫在哪、衝突時聽誰的」。

## 目錄

- SOLID
- Clean Code
- Specification-Driven Development(契約先行)
- Test-Driven Development / ATDD / ISTQB
- 衝突仲裁順序

## SOLID

本專案的依賴方向鐵律就是 SOLID 的具體化,權威在 `AGENTS.md` §Non-Negotiable Architecture:

- **依賴反轉(DIP)**:`Bridge / Form1(Host) → Application → Domain ← Infrastructure`。Application 不得引用 Infrastructure;跨層共用契約(`JetActionException`、`JetErrorCodes`、`JetJsonStorage`)放 Domain。唯一文件化例外:`Infrastructure/DemoWorkbookWriter` 實作 Application 的 dev-only port `IDemoFileWriter`。
- **單一職責(SRP)**:`Form1.cs` 是 thin WebView2 host(無業務邏輯);`Bridge/` 只做 WebMessage JSON 傳輸與 action dispatch;`Application/` 擁有 use case;`Domain/` 純規則、framework-free;`Infrastructure/` 擁有 file I/O 與 provider-specific SQL。
- **開放封閉 / 里氏替換**:同一 Domain repository 介面下,SQLite 與 SQL Server 為可替換實作;provider 分支只在 Infrastructure(`ProviderRouting*`),handler 只見介面、不感知 provider(`docs/jet-guide.md` §13)。

落地檢查:新增程式碼前,先確認它該落在哪一層、引用方向是否合法。檔案變大常是 SRP 失守的訊號。

## Clean Code

權威散見 `AGENTS.md` 與既有程式風格,重點:

- 命名與註解跟著周圍程式碼走;註解只寫規格依據與不變量,不寫流水帳。
- 斷言/邏輯鎖可觀察行為,不鎖實作細節(見 jet-testing §3 fragile test)。
- 魔術數字要自我說明或附 guide 條文出處。
- 文件寫作另有規範:`docs/README.md`(禁流水代號、描述性命名、狀態標示、領域名詞精確)。

## 工程品味準則

跨子專案一貫採用的工程取捨基準(自匯出底稿里程碑沿用至今)。這些在本專案沒有其他權威出處,以本節為準:

- **資料結構優先**:先把資料結構與型別設計對,讓特例自然消失,而不是用分支補特例(data-structure-first)。
- **深模組、窄介面**:介面小、實作深;對外只露必要的窄契約,複雜度藏在實作裡(例:`WorkpaperWriter` 的 `WriteAsync → ExportStats`)。實作相對於介面要夠厚才划算。
- **DRY 門檻=3**:同一段邏輯出現第三次才抽共用;前兩次先容忍重複,避免過早抽象。
- **註解寫「為什麼」**:只寫規格依據、不變量、取捨理由,不複述程式碼在做什麼。
- **不碰無關程式碼**:一次只改本任務範圍;順手看到的其他問題記成待辦清單(ledger),不夾帶進本次變更。
- **複審分級**:問題照 Critical / Important / Minor 分級回報,品味類問題一樣套這個嚴重度標尺。

## Specification-Driven Development(契約先行)

本專案版的「規格先於實作」,權威在 `AGENTS.md` §Contract-First Workflow 與 `docs/action-contract-manifest.md`:

1. 動 WebView2 bridge、workflow UX、或任何 backend action 前,先讀 manifest。
2. 盡量重用既有 action。
3. 需要新資料/行為時,**先更新 manifest** 再實作。
4. 落地後回寫受影響的現況規格(雙向回流,防漂移)。

對應 jet-testing 的 ATDD:Application 層測試即驗收測試,驗收條件來自 manifest 的 payload / response / error code。新增 action 的順序是:改 manifest → 寫紅燈驗收測試 → 實作 handler。

## Test-Driven Development / ATDD / ISTQB

**唯一權威是 jet-testing skill**,本地圖不重述其內容。它涵蓋:測試金字塔與層級歸屬、FIRST、禁止的 test smells、設計技術(BVA/等價分割/決策表/狀態轉換/pairwise)、oracle 策略、mutation 自查、風險排序、prompt 模板。規則類(Domain)優先 test-first。

開發迴圈中的位置:見 SKILL.md 階段 2(紅燈)與階段 3(綠燈)。

## 衝突仲裁順序

同一主題出現多處說法時,以下優先(高到低):

1. 使用者明確指示。
2. `docs/jet-guide.md`(領域與架構最深權威)。
3. 對應主題的現況規格:契約看 `action-contract-manifest.md`、測試看 jet-testing、架構邊界看 `AGENTS.md`。
4. 本 skill 與本地圖(只做編排與指路)。

本地圖與任一權威衝突時,以權威為準並回報歧義——不在本檔另立第二份事實。
