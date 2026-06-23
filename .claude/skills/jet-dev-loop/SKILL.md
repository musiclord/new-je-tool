---
name: jet-dev-loop
description: JET 專案 C#/.NET 開發迴圈的執行規範。在 src/JET 進行建置、測試、執行時偵錯,或任何功能與修補開發時讀。明訂 agent 如何自跑 build/test、如何讀執行時診斷日誌、以及與架構鐵律與測試紀律的銜接。
---

# JET 開發迴圈 (Build → Test → Runtime Debug)

用 VSCode 加 Claude Code 開發這個專案時,agent 的開發迴圈以本文件為準。核心原則只有一條:只要某個環節能讓 agent 自己執行、自己讀結果、自己修正,就不要把它丟回給使用者手動操作。具體來說,build 和 test 都由 agent 直接跑;執行時的真實行為則透過診斷日誌檔讀取,不必再靠人手動複製貼上。

本文件負責的是「迴圈怎麼編排」,不是「規則的全文」。每一段紀律背後都有既有的權威文件,本文件只把它們串接起來並指路。萬一發生衝突,以被指向的那份權威為準,並把歧義回報出來。

## 何時讀本文件

- 在 `src/JET` 進行任何功能開發、修補或重構。
- 要建置、跑測試,或驗證執行時行為。
- 規劃一段工作要怎麼推進,例如先寫測試還是先改契約。

**何時不需要**:純前端視覺設計的工作改讀 minimalist-ui。純測試撰寫的細節改讀 jet-testing,本文件只在迴圈層級指向它,不重述其內容。

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

**0 定位** — 先讀 `AGENTS.md` 的 Read Order 與 File Map。確認這次改動落在 Bridge、Application、Domain、Infrastructure 哪一層,以及它要走的依賴方向是否被允許。不要靠臆測:repository 才是唯一的事實來源,猜測等於違規。

**1 契約先行(Spec-Driven)** — 這次改動如果碰到「前端 ↔ WebView2 ↔ C# handler」之間傳遞的資料形狀(wire shape),那就要**先**更新 `docs/action-contract-manifest.md`,再動手實作。能重用既有的 action 就不要新增。這就是本專案版本的「規格先於實作」。

**2 紅燈** — 先決定這個測試該落在哪一層(Domain、Application、Infrastructure 或 Bridge),再寫一個會失敗的測試。順序很重要:**先跑這個新測試,確認它真的失敗(紅燈),把失敗的輸出留下來當證據,然後才動實作。** 如果測試一寫就綠,多半代表它根本沒驗到新行為。原則是「用證據,不用嘴」——別只說一句綠了,要拿輸出證明。至於 oracle(預期值來源)、設計技術、負向情境怎麼處理,一律依 jet-testing。**REQUIRED SUB-SKILL:在撰寫、修改或評估測試之前,先讀 jet-testing skill。**

**3 綠燈** — 把實作做到測試轉綠。下列指令由 agent 自己執行並讀輸出,不要請使用者代跑或貼結果回來:

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test  src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

(完整的權威清單見 `AGENTS.md` §Verification Commands。第一次跑、或需要還原相依套件時,先執行 `dotnet restore src/JET/JET.slnx`。)快速回饋是這個迴圈的命脈,所以一旦 build 或 test 變慢、卡住,先把工具鏈問題解決掉再繼續,不要硬撐。

測試轉綠之後,還要再加一道突變測試,確認這些測試「真的抓得到 bug」而不只是剛好綠燈。時機是**在每個階段性或里程碑之前**跑,不是每個小任務都跑。對象是本輪改動的 Domain 與 Application 檔:`dotnet stryker --since:main -m <改動的 .cs 檔>`(設定見 `stryker-config.json`;一律同時用 `-m` 把改動檔指明出來)。跑完之後,凡是存活下來的 mutant,代表測試沒守住,要補上斷言。節奏與門檻見 jet-testing §6。

**4 執行時驗證** — 有些執行時行為自動化測試覆蓋不到,例如 WinForms 加 WebView2 的 GUI、實際的 action 生命週期、SQL 與參數、transaction、未預期的 exception。這時讀 `references/runtime-debug-bridge.md`,決定要走「讀診斷日誌檔」還是 `/verify`。GUI 互動本身仍然要靠使用者或 `/verify` 來做,但操作後產生的日誌真相,由 agent 自己讀。

**5 重構** — 改善結構的同時,依賴方向、契約、`data-bind` 識別碼都要維持不變。不要把業務邏輯外移到 `Form1.cs`、HTML、CSS 或 JS。

**6 收尾** — 回報三件事:做了什麼、驗證了什麼、跳過了什麼以及為什麼跳過。**不要自行 commit 或 push,也不要主動提議 commit。** 版控的時機由使用者決定(見 `AGENTS.md` §Version Control)。如果有些項目只能在 Windows 端才驗得了,把它們寫進 `docs/windows-handoff.md`。

## 原則對照

SOLID、Clean Code、Spec-Driven、TDD 在本專案各有具體落地方式與權威出處,整理在 `references/principles-map.md`。本文件不重述這些原則的內容。

## 常見錯誤

- **跳過契約先行**:直接改 handler 或 payload,事後才補 manifest。結果就是規格和實作漂移開來。正確做法是先改 manifest。
- **要使用者代跑 build/test 或貼結果回來**:這兩件事 agent 應該自己跑、自己讀。需要人手動複製貼上的,只有 GUI 互動這種 agent 觀察不到的情況。
- **把執行時偵錯誤解成要去驅動 GUI**:agent 不自動化 GUI。agent 做的是被動地讀診斷日誌檔。要驅動 GUI 時,走 `/verify` 或請使用者操作。
- **在迴圈裡重述測試規則**:測試紀律歸 jet-testing 管,本文件只指路。重述會造成兩份事實互相打架。
- **沒跑驗證就宣稱「已完整測試」或「已修好」**:要先有輸出證據,才能下結論。
- **憑記憶寫 SDK 或函式庫的 API**:OpenXML、.NET BCL、第三方套件的型別與方法簽章,一律用 `microsoft-docs` 或 `microsoft-code-reference` skill 查證,不要靠記憶。記錯簽章會編譯過、但行為錯,是很難發現的隱性 bug 來源。

## 邊界(本 skill 不涵蓋)

- publish 與 deploy 的自動化(這類 GUI 嚮導本輪刻意不納入)。
- WinForms 與 WebView2 的 E2E 自動化測試(這會牴觸 jet-testing 的硬邊界)。
- 把任何業務規則計算移出 Application、Domain 或 Infrastructure。
