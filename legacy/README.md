# Legacy — JET 歷史實作歸檔

本目錄集中 JET 舊系統的歷史產物，包含 Caseware IDEA / IDEAScript、Excel VBA、Access 與輔助工具。所有檔案**僅供業務邏輯與規則對照**，不進入新系統建置路徑。

> 正式開發已遷移至 `.NET 10 + WinForms + WebView2 + HTML + SQLite + SQL Server` — 見專案根目錄 [`README.md`](../README.md) 與 [`docs/jet-guide.md`](../docs/jet-guide.md)。

---

## 目錄結構

| 項目 | 角色 | 來源 |
|:---|:---|:---|
| `ideascript.bas` | Caseware IDEA 時期的完整 IDEAScript (11,379 行) | IDEA archive |
| VBA source archive | Excel VBA 原始碼與工作簿 | VBA archive |
| `SqlBuilder.xlsm` | SQL 建構輔助工具 | VBA archive |
| `jet-legacy-notes.md` | IDEA/VBA 時期審計方法論、資料管道與工作底稿結構整理(已清洗,僅供對照) | 開發筆記整理 |

---

## 為什麼棄用

### Caseware IDEA + IDEAScript

**棄用原因**：不再訂閱 IDEA 授權。

- IDEA 為商用審計資料分析平台，依賴獨家 `.IDM` 檔案格式與 `client.OpenDatabase` 等 IDEA 專有 API
- 無法在不訂閱的前提下持續運行或部署
- IDEAScript 的語法接近 BASIC，缺乏現代 IDE 支援、單元測試、AI 協作

### Excel VBA + Access Database

**棄用原因**：生態限制累積到無法承擔。

| 面向 | VBA + Access 的問題 | 新技術棧的解法 |
|:---|:---|:---|
| 資料量 | Access `.accdb` 單檔 2GB 上限；大量 GL/TB 會爆 | SQL Server 支援數千萬筆，SQLite 處理本機運算 |
| AI 協作 | VBA 幾乎無 AI agent 支援，無法自動 build / test / refactor | .NET + C# 有 Copilot Agent Mode、Codex CLI、Claude Code 完整工具鏈 |
| 測試 | VBA 沒有主流單元測試框架；只能靠 Excel 手動跑 | `dotnet test` + xUnit，CI/CD 友善 |
| 打包 | `.xlsm` 一定要有 Excel 才能跑，且受巨集安全策略影響 | Single-file `.exe` self-contained deployment |
| UI | UserForms 客製能力極低，AI 無法生成 | HTML/CSS/JS + WebView2，AI 直接迭代 |
| 維護 | VBA 資源稀少、招人困難、官方演進停滯 | C# + .NET 10 LTS 為現代 MS 生態主流 |
| 資安 | `.xlsm` 巨集需公司資安通報 | 單機 `.exe`，不架 server、不開 port |

> **備註**：公司環境禁用 Python 作為正式方案，所以替代選項實際上只剩 .NET 生態。

---

## VBA + Access 架構摘要

VBA 實作採 Presenter-based 分層，邏輯不寫在 Sheet / UserForm 事件內。

```
User
  │
  ▼
View (UserForm .frm)  ──events──▶  Presenter (.cls)
                         ◀──update UI──
                                │
                                ▼
                         Service (業務邏輯)
                          │
                          ▼
                         DbAccess (DAO)
                          │
                          ▼
                         Access .accdb
```

### 模組分層

| 層 | 檔案樣式 | 職責 |
|:---|:---|:---|
| View | `View*.frm` | 純顯示、轉發使用者操作給 Presenter |
| Presenter | `Presenter*.cls` | 事件處理、呼叫 Service、回填 View |
| Service | `Service*.cls` | 業務邏輯 (匯入、驗證、篩選、匯出)，不依賴 UI |
| DataAccess | `DbAccess.cls`、`DbSchema*.cls` | DAO 操作、Transaction 管理、schema 建立 |
| Mapper | `MapperField*.cls` | 欄位標準化映射 |
| Core | `Core*.cls` | SQL Builder、Logger、Context 等基礎設施 |

### 技術選型

| 項目 | 選擇 | 原因 |
|:---|:---|:---|
| 前端 | Excel VBA UserForms | 與 Office 生態整合 |
| 後端 | Microsoft Access Database Engine `.accdb` | 單檔案部署、DAO 原生整合 |
| 連線介面 | DAO (Data Access Objects) | 對 Access 效能優於 ADO、支援 TableDefs 動態建表、Transaction 完整 |
| 版本控制 | Git + Python 腳本匯出 `.cls` / `.bas` / `.frm` | Excel `.xlsm` 不利 diff |

### 開發慣例 (僅供對照)

| 前綴 | 意義 | 範例 |
|:---|:---|:---|
| `m_` | 類別成員變數 | `m_dal` |
| `p_` | 函式參數 | `p_FilePath` |
| `i_` | 介面 | `i_Repository` |

依賴注入採**模擬建構子** (`Initialize(ByVal dal As DbAccess)`) 或屬性注入，因 VBA 類別不支援建構子參數。

### 已知陷阱 (供遷移時避開)

- **DAO 緩存**：新建表後 `TableDefs` 未刷新可能看不到
- **`Set` 關鍵字**：物件指派遺漏 `Set` 會觸發預設屬性指派錯誤
- **大量寫入**：未包 `BeginTrans` / `CommitTrans` 效能差一個數量級
- **Excel Cells 迴圈**：應改用 array 記憶體內處理

---

## IDEAScript 規則對照

`ideascript.bas` 的函式地圖（供遷移規格對照）：

| 函式 | 階段 | 對應新架構概念 |
|:---|:---|:---|
| `Main` / `Intro_Dlg` / `TBDetail_Dlg` / `GLDetail_Dlg` | 步驟 1 UI | Frontend (`jet-template.html` Page 1) |
| `Step1_Validation` / `Step1_Export_INFFile` | 資料驗證 | Application Command: `ValidateImportCommand` |
| `Step2_Upload_AccountMapping_File` / `Step2_Upload_Holiday_File` / `Step2_Upload_MakeUpday_File` | 輔助檔案 | `UploadAuxiliaryCommand` |
| `Step3_Routines` | R1-R8 + A2-A4 執行 | `RunPreScreenCommand` + 每條規則對應 handler |
| `Step4` / `Step4_JoinDatabase` / `Step4_Summarization` | 進階篩選 | `RunAdvancedFilterCommand` |
| `Step5_Export_Excel_TW` / `Step5_WPdata_Collection` | 工作底稿產出 | `ExportWorkPaperCommand` |
| `X_Update_Project_Info` / `X_Get_Project_Info` 等 `X_*` | 專案元資料 CRUD | `ProjectRepository` |
| `Z_DirectExtractionTable` / `Z_renameFields` / `Z_Rename_DB` / `Z_File_Exist` 等 `Z_*` | IDEA 低階檔案與欄位操作 | Infrastructure 層，以 `IGlRepository.QueryByRule()` 取代 |

> **新系統不應該逐段翻譯 `ideascript.bas`**，而是依 [`docs/jet-guide.md`](../docs/jet-guide.md) 的「規則聲明式規格」重新實作。
> `ideascript.bas` 的角色從「實作來源」降格為「業務規則與邊界條件的對照原本」。

---

## 使用本目錄的建議

- **業務規則確認** — 讀 `docs/jet-guide.md` 的規則章節；只有當規格有歧義時，才回頭查 legacy 原始碼
- **欄位標準化對照** — 查 legacy mapper 類別了解原本如何映射
- **SQL 樣板參考** — 查 legacy SQL builder 類別作語意對照
- **UI 流程對照** — 查 legacy view 檔案；新 UI 以 `docs/jet-frontend-description.md` 與 `src/JET/JET/wwwroot/` 為準

**不要做的事**：
- 不要把 `.cls` / `.bas` 的程式碼直接翻成 C#
- 不要把 VBA 的 Presenter-based 分層原樣搬進 .NET (新系統用 Application CQRS)
- 不要以為 Access SQL 語法與 SQL Server / SQLite 完全相容（方言不同）
