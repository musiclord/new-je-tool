# JET Copilot 守則

架構變更前先讀 `AGENTS.md` 與 `docs/jet-guide.md`。**完整的架構鐵律與理由以 `AGENTS.md` → `docs/jet-guide.md` 為唯一權威**;本檔只是給 Copilot 的精簡子集,不是第二份事實來源。

架構摘要(細節見 `AGENTS.md` 的 Non-Negotiable Architecture):

- 依賴方向固定:WinForms + WebView2 薄殼(`Form1.cs`)/ `Bridge` → `Application` → `Domain` ← `Infrastructure`;`Domain` 保持無框架相依。
- provider 分支與所有 GL/TB 計算(驗證、預篩選、進階篩選、匯出)一律走後端參數化集合式 SQL,**不得**寫在 JavaScript,也**不得**在 C# 用 LINQ 掃整個母體。
- 前端只透過 `JetApi` 邊界呼叫後端;前端執行期檔案放 `src/JET/JET/wwwroot/`。

工具專屬硬邊界(本檔負責守住,不靠指路):

- 不改 action 名稱或 payload 形狀,除非先更新 `docs/action-contract-manifest.md`。
- 不編輯 WinForms designer 產生檔,除非被明確要求。
- 不自行發動或主動提議 `git commit` / `git push`;版控只在使用者自行驗證成果後、明確下令時才執行。commit 訊息不得自行加上 `Co-Authored-By`、"Generated with" 或任何 AI 署名,除非使用者明確要求。詳見 `AGENTS.md` 的 Version Control 章節。
