# Journal Entry Testing (JET)

審計用的**日記帳分錄測試**工具 — 依 ISA 240 / ISA 330 對管理階層凌駕控制風險進行全母體分錄篩選。

**技術棧**：`.NET 10 + WinForms + WebView2 + HTML/CSS/JS + SQLite + SQL Server`

**深度文件**：所有業務規則、系統架構、資料策略與 AI 協作指南都在 [`docs/jet-guide.md`](docs/jet-guide.md)。**寫程式前讀這個。**

**Agent 入口**：跨工具的 AI 上下文索引在 [`AGENTS.md`](AGENTS.md)，Claude Code 入口在 [`CLAUDE.md`](CLAUDE.md)，Copilot repository 指引在 [`.github/copilot-instructions.md`](.github/copilot-instructions.md)，前後端契約總表在 [`docs/action-contract-manifest.md`](docs/action-contract-manifest.md)。

---

## 專案結構

```
je-testing/
├── README.md                 # 本檔：識別、快速開始、目錄
├── AGENTS.md                 # Agent 入口索引：先讀這個，再進 docs/
├── CLAUDE.md                 # Claude Code 專用入口
├── global.json               # 固定 .NET 10.0.201 feature band
├── .claude/
│   └── skills/               # Claude Code project skills
├── .github/
│   └── copilot-instructions.md  # Repository-wide Copilot 指引
├── docs/
│   ├── README.md             # 文件地圖與寫作規範：新增/修改文件前先讀
│   ├── jet-guide.md          # 單一深度指南 (領域 + 架構 + 規則規格 + AI workflow)
│   ├── action-contract-manifest.md # 前端/WebView2/C# action 契約總表
│   ├── jet-frontend-description.md # 前端介面規格
│   ├── development-status.md # 開發現況快照 (可用功能/進行中/規劃中/技術債)
│   ├── development-log.md    # 開發紀錄 (依日期、只增不改)
│   ├── windows-handoff.md    # Windows 端驗證任務的滾動交接
│   ├── specs/                # 各輪開發的設計提案 (日期+主題命名)
│   └── jet-template.html     # 前端 UI 參考模板
├── data/                     # 範例測試資料 (GL / TB / 假日 / 補班日)
├── src/JET/
│   ├── JET.slnx              # Visual Studio / dotnet 方案檔
│   └── JET/                  # WinForms + WebView2 應用程式專案
└── legacy/                   # 舊系統歸檔，僅供規則語意與欄位對照
```

---

## 快速開始

### 建置 WinForms 專案

1. 以 **Visual Studio 2026** 開啟 [`src/JET/JET.slnx`](src/JET/JET.slnx)
2. 確認已安裝 **.NET 10 SDK** 與 **Windows Desktop / WinForms** workload
3. `dotnet build src/JET/JET.slnx` 或在 VS 直接 F5

> `Form1` 應維持極薄 host，只負責 WinForms 視窗、WebView2 生命週期與 Bridge 初始化。實作邊界見 [`docs/jet-guide.md` §14](docs/jet-guide.md#14-專案結構規劃)。

### 開發環境建議

| 平台 | 用途 |
|:---|:---|
| Windows + Visual Studio 2026 + Copilot Agent Mode | 主場：WinForms / WebView2 整合、Designer、最終打包 |
| Mac / Linux + VS Code / Claude Code / Codex CLI | 副場：Domain / Application / HTML 前端 / SQL / 測試 |

---

## 技術定位

正式系統以 **.NET 10 + WinForms + WebView2 + HTML/CSS/JS + SQLite + SQL Server** 重新構建。

舊 IDEA / VBA / Access 內容集中在 [`legacy/`](legacy/)，只作規則語意與欄位對照，不作為新系統的實作來源。

技術約束、排除選項與架構決策細節見 [`docs/jet-guide.md` §9-10](docs/jet-guide.md#9-技術約束與排除選項)。

---

## 架構一句話

```
HTML 前端 ─action+payload→ Thin Bridge ─dispatch→ Application (CQRS)
                                                      │
                                                      ▼
                                          Domain (純邏輯 + Repository 介面)
                                                      │
                                   ┌──────────────────┴──────────────────┐
                                   ▼                                     ▼
                           SqliteGlRepository                 SqlServerGlRepository
```

- **Thin-Bridge Action-Dispatcher**：前後端之間只傳 JSON，邏輯不夾在 Bridge 裡
- **Application CQRS**：Commands (變更) 與 Queries (讀取) 分離；每條規則一個 Handler
- **Clean Core**：Domain 無 I/O 依賴；Infrastructure 實作 Domain 介面
- **雙 Provider**：SQLite 與 SQL Server 共用同一 `IGlRepository` 介面，執行期依設定切換；SQLite 是本機 provider，SQL Server 是 large-data provider

詳見 [`docs/jet-guide.md` §11-13](docs/jet-guide.md#11-架構總覽)。

---

## 核心原則

1. 業務邏輯不進 `Form1` — Host 極薄
2. 前端只送 `action + payload`，不拼 SQL
3. 每條規則一個 Command/Query + Handler，不做大函式
4. Repository 介面只有一份，Provider 兩份，方言差異在 Infrastructure 處理
5. AI 可自由改 UI 外觀，但**不可改** action 契約 / fixed binding ID / Designer.cs
6. 所有使用者輸入走參數化查詢，拒絕字串拼接 SQL
7. 寫程式前讀 [`docs/jet-guide.md`](docs/jet-guide.md)；別回頭翻 11,000 行的 `legacy/ideascript.bas`
