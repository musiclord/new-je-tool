# Journal Entry Testing (JET)

這是一套審計用的日記帳分錄測試（Journal Entry Testing，JET）工具。它依 ISA 240 與 ISA 330 兩號審計準則，針對「管理階層凌駕控制」這項風險，對整個母體的分錄做篩選，而不是抽樣。

**技術棧**：`.NET 10 + WinForms + WebView2 + HTML/CSS/JS + SQLite + SQL Server`

**深度文件**：所有業務規則、系統架構、資料策略與 AI 協作指南都集中在 [`docs/jet-guide.md`](docs/jet-guide.md) 這一份文件裡。動手寫程式之前請先讀它。

**Agent 入口**：跨工具共用的 AI 上下文索引在 [`AGENTS.md`](AGENTS.md)。各工具另有自己的入口：Claude Code 看 [`CLAUDE.md`](CLAUDE.md)，Copilot 的 repository 指引在 [`.github/copilot-instructions.md`](.github/copilot-instructions.md)。前後端之間的契約總表則在 [`docs/action-contract-manifest.md`](docs/action-contract-manifest.md)。

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

> `Form1` 必須維持成一個極薄的宿主（host），只負責三件事：管理 WinForms 視窗、管理 WebView2 的生命週期，以及初始化 Bridge。業務邏輯不進 `Form1`。實作邊界見 [`docs/jet-guide.md` §14](docs/jet-guide.md#14-專案結構規劃)。

### 開發環境建議

| 平台 | 用途 |
|:---|:---|
| Windows + Visual Studio 2026 + Copilot Agent Mode | 主場：WinForms / WebView2 整合、Designer、最終打包 |
| Mac / Linux + VS Code / Claude Code / Codex CLI | 副場：Domain / Application / HTML 前端 / SQL / 測試 |

---

## 技術定位

正式系統以 **.NET 10 + WinForms + WebView2 + HTML/CSS/JS + SQLite + SQL Server** 全新構建。

上一代系統用 IDEA、VBA 與 Access 寫成，那些內容現在集中歸檔在 [`legacy/`](legacy/)。保留它們只是為了對照規則語意與欄位定義，不能拿來當新系統的實作藍本。

技術約束、被排除的技術選項，以及架構決策的細節，都在 [`docs/jet-guide.md` §9-10](docs/jet-guide.md#9-技術約束與排除選項)。

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

- **Thin-Bridge Action-Dispatcher**：前後端之間只傳 JSON。Bridge 本身不放任何業務邏輯，它只負責把 action 分派出去。
- **Application 層採 CQRS（命令查詢職責分離）**：把「會改資料的命令（Command）」和「只讀資料的查詢（Query）」分開；每一條審計規則對應一個 Handler。
- **Clean Core（乾淨核心）**：Domain 層不依賴任何 I/O；由 Infrastructure 層去實作 Domain 定義的介面。
- **雙 Provider（兩套資料庫實作）**：SQLite 與 SQL Server 共用同一個 `IGlRepository` 介面，執行期再依設定決定用哪一套。SQLite 是本機用的 provider，SQL Server 則是處理大資料量時用的 provider。

詳見 [`docs/jet-guide.md` §11-13](docs/jet-guide.md#11-架構總覽)。

---

## 核心原則

1. 業務邏輯不進 `Form1`，宿主層維持極薄。
2. 前端只送出 `action + payload`，前端不自己拼 SQL。
3. 每一條規則對應一個 Command 或 Query 加一個 Handler，不要寫成一個大函式。
4. Repository 介面只有一份，Provider 有兩份；兩種資料庫的方言差異一律在 Infrastructure 層吸收。
5. AI 可以自由更動 UI 外觀，但不可以改動 action 契約、固定的 binding ID，或 Designer.cs。
6. 所有使用者輸入一律走參數化查詢，禁止用字串拼接 SQL。
7. 寫程式前先讀 [`docs/jet-guide.md`](docs/jet-guide.md)，不要回頭去翻那份 11,000 行的 `legacy/ideascript.bas`。
