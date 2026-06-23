# 執行時偵錯橋接

自動化測試覆蓋不到執行時行為:WinForms + WebView2 的 GUI 互動、實際 action 生命週期、跨層 SQL+參數、transaction、未預期的 exception。本檔說明 agent 怎麼在不驅動 GUI 的前提下取得這些真相。

## 兩件事分開

| 要驗的東西 | 誰做 | agent 角色 |
|:---|:---|:---|
| GUI 互動(點按鈕、看畫面、操作流程) | 使用者,或 `/verify` | agent 不自動化 GUI |
| 執行時系統真相(action/SQL/transaction/exception/milestone) | 診斷日誌 | agent 直接讀日誌 |

關鍵:**agent 不點 GUI,但 GUI 操作後產生的日誌真相,agent 自讀。** 這條界線與 jet-testing「禁止 WebView2/WinForms E2E 自動化」一致。

## 診斷日誌是什麼

第三層、dev-only 日誌(與 `result_*` 審計表、`IMessageLogStore` 的 UX 訊息相互獨立)。**只在 Debug 組建啟用**(`enableDevTools`);Release 整條 no-op,不產生任何日誌。每筆 `DiagnosticLogEntry` 含:時間戳、層級、category、event 名、訊息、`correlation_id` / `transaction_id` / `project_id`、結構化 `fields`(SQL、參數、`duration_ms`、`rows_affected`…)、exception。可用 `correlation_id` 把單一 action 跨層串起來追。

## 取得日誌的兩條路徑

### 主路徑:讀診斷日誌檔(預設,免手動複製)

Debug 啟動時,檔案 sink(`NdjsonFileLoggerProvider`)把每筆日誌即時 append 到固定路徑,agent 直接讀:

```
%LOCALAPPDATA%\JET\logs\jet-dev-<啟動時間戳>.ndjson
```

流程:使用者以 Debug 啟動程式(VSCode F5 的 `.NET Core Launch`,或 `dotnet run --project src/JET/JET`)並操作要驗的流程 → agent `Read` / `Grep` 最新的 `.ndjson` 檔(每次啟動一檔,取最新時間戳)比對行為。每行一筆完整 JSON,可直接 `Grep` `correlation_id` 或事件名。Release 組建不產生此檔(整條日誌 no-op)。

### 備援路徑:DEV 面板匯出

需要當下記憶體快照(或不便取檔)時,DEV 面板「診斷日誌」按「重新整理」(`dev.log.export` 回完整 NDJSON)→「複製」貼給 agent。內容與檔案一致(共用 `DiagnosticNdjson` 序列化),差別只在這條需一次手動傳遞。

## agent 讀日誌時看什麼

- 用 `correlation_id` 聚焦單一 action 的完整生命週期,確認跨層順序與結果。
- 比對 `fields` 內的 SQL 與參數,驗證規則確實以參數化 set-based SQL 執行(`docs/jet-guide.md` §1.5.2),而非在 C# 做 LINQ。
- 檢查 exception 欄位與錯誤碼,對照 `docs/action-contract-manifest.md` 的 error shape。
- 金額相關行為對照 scaled 整數運算的不變量(高風險區,見 jet-testing §7)。

## 何時用 `/verify`

需要實際操作 GUI 才能確認的行為(版面、互動、WebView2 前後端往返的視覺結果),走 `/verify` 或請使用者操作。診斷日誌補的是「操作之下系統做了什麼」,不是「畫面長怎樣」。

## 邊界

- 不為了讀日誌而自動化 GUI 點擊。
- Release 組建沒有診斷日誌——驗執行時行為一律用 Debug 組建。
- 檔案 sink 是被動寫檔的 dev 工具,失敗須自我吞納,不得影響主程式。
