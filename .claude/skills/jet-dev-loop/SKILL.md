---
name: jet-dev-loop
description: JET 專案 C#/.NET 開發迴圈的執行規範。在 src/JET 進行建置、測試、執行時偵錯,或任何功能與修補開發時讀。明訂 agent 如何自跑 build/test、如何讀執行時診斷日誌、以及與架構鐵律與測試紀律的銜接。
---

# JET 開發迴圈 (Build → Test → Runtime Debug)

VSCode + Claude Code 開發此專案時,agent 的開發迴圈以本文件為準。核心原則:**能讓 agent 自跑、自讀、自我修正的環節,就不要丟回給使用者手動操作。** build 與 test 由 agent 直接執行;執行時真相經診斷日誌檔讀取,免去手動複製貼上。

本 skill 是「迴圈的編排」,不是「規則全文」。每段紀律都有既有權威,本文件只負責串接與指路。遇衝突,以被指向的權威為準並回報歧義。

## 何時讀本文件

- 在 `src/JET` 進行任何功能開發、修補、重構。
- 要建置、跑測試、或驗證執行時行為。
- 規劃一段工作要怎麼推進(先測試還是先契約)。

**何時不需要**:純前端視覺設計(讀 minimalist-ui);純測試撰寫細節(讀 jet-testing,本文件只在迴圈層指向它)。

## 迴圈總覽

| 階段 | 做什麼 | 權威 / 工具 |
|:---|:---|:---|
| 0 定位 | 依任務讀對應權威文件,確認落在哪一層 | `AGENTS.md` Read Order、`docs/jet-guide.md` |
| 1 契約先行 | 動 action/payload/workflow 前,先改 manifest | `docs/action-contract-manifest.md` |
| 2 紅燈 | 先寫會失敗的測試(規則類 test-first) | jet-testing skill |
| 3 綠燈 | agent 自跑 build + test,讀輸出迭代到綠 | 本文件「驗證指令」 |
| 4 執行時驗證 | 讀診斷日誌檔 / `/verify` 驗 GUI | `references/runtime-debug-bridge.md` |
| 5 重構 | 清理,邊界與契約不破 | `AGENTS.md` 架構鐵律 |
| 6 收尾 | 回報成果與驗證狀態,版控留給使用者 | `AGENTS.md` §Version Control |

## 各階段要點

**0 定位** — 先讀 `AGENTS.md` 的 Read Order 與 File Map,確認改動落在 Bridge / Application / Domain / Infrastructure 哪一層,以及依賴方向是否允許。臆測等於違規:repository 是唯一事實來源。

**1 契約先行(Spec-Driven)** — 若改動觸及前端 ↔ WebView2 ↔ C# handler 的 wire shape,**先**更新 `docs/action-contract-manifest.md` 再實作。能重用既有 action 就不要新增。這是本專案版的規格先行。

**2 紅燈** — 先決定測試層級(Domain/Application/Infrastructure/Bridge),再寫會失敗的測試。**先跑新測試、確認它真的失敗(紅),把失敗輸出留作證據,再動實作**;一寫就綠多半代表它沒驗到新行為(evidence over assertion——不嘴上說綠,拿輸出證明)。oracle、設計技術、負向情境一律依 jet-testing。**REQUIRED SUB-SKILL:撰寫、修改、評估測試前讀 jet-testing skill。**

**3 綠燈** — 實作到測試轉綠。agent 自行執行下列指令並讀輸出,不需使用者代跑或貼結果:

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test  src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

(權威清單見 `AGENTS.md` §Verification Commands;首次或還原相依時先 `dotnet restore src/JET/JET.slnx`。)快回饋是迴圈命脈——若 build/test 變慢或卡住,先解決工具鏈再繼續。

綠燈之後,在**階段性／里程碑前**(不是每個小任務)對本輪改動的 Domain/Application 檔跑一次突變測試,確認測試「真的抓得到 bug」而非只是綠燈:`dotnet stryker --since:main -m <改動的 .cs 檔>`(設定見 `stryker-config.json`,一律同時用 `-m` 指定改動檔)。存活的 mutant 補上斷言。節奏與門檻見 jet-testing §6。

**4 執行時驗證** — 自動化測試覆蓋不到的執行時行為(WinForms + WebView2 GUI、實際 action 生命週期、SQL+參數、transaction、exception)。讀 `references/runtime-debug-bridge.md` 決定走「讀診斷日誌檔」或 `/verify`。GUI 互動本身仍需使用者或 `/verify`,但日誌真相 agent 自讀。

**5 重構** — 改善結構時保持依賴方向、契約、`data-bind` 識別碼不變。不把業務邏輯外移到 `Form1.cs`/HTML/CSS/JS。

**6 收尾** — 回報「做了什麼、驗證了什麼、跳過什麼及原因」。**不自行 commit/push,也不主動提議 commit**;版控時機由使用者決定(`AGENTS.md` §Version Control)。Windows 端才能驗的項目,寫進 `docs/windows-handoff.md`。

## 原則對照

SOLID、Clean Code、Spec-Driven、TDD 在本專案各有落地與權威出處,見 `references/principles-map.md`。本文件不重述原則內容。

## 常見錯誤

- **跳過契約先行**:直接改 handler/payload,事後才補 manifest → 規格與實作漂移。先改 manifest。
- **要使用者代跑 build/test 或貼結果**:agent 應自跑自讀。手動複製貼上只在 GUI 互動這類 agent 無法觀察處才需要。
- **把執行時偵錯誤解成要驅動 GUI**:agent 不自動化 GUI;agent 做的是「被動讀診斷日誌檔」。驅動 GUI 走 `/verify` 或使用者。
- **在迴圈裡重述測試規則**:測試紀律是 jet-testing 的事,本文件只指路,避免雙重事實。
- **宣稱「已完整測試/已修好」卻沒跑驗證**:先有輸出證據,再下結論。
- **憑記憶寫 SDK / 函式庫 API**:OpenXML、.NET BCL、第三方套件的型別與方法簽章一律用 `microsoft-docs` / `microsoft-code-reference` skill 查證,不靠記憶——記錯簽章是會編譯過卻行為錯的隱性 bug 來源。

## 邊界(本 skill 不涵蓋)

- publish / deploy 自動化(GUI 嚮導類,本輪刻意不納)。
- WinForms / WebView2 的 E2E 自動化測試(與 jet-testing 硬邊界衝突)。
- 任何業務規則計算移出 Application/Domain/Infrastructure。
