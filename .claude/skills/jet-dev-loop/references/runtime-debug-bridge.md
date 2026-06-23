# 執行時偵錯橋接

有些執行時行為,自動化測試覆蓋不到:WinForms 加 WebView2 的 GUI 互動、實際的 action 生命週期、跨層的 SQL 與參數、transaction、未預期的 exception。本檔說明 agent 怎麼在「不驅動 GUI」的前提下,取得這些真相。

## 兩件事要分開

| 要驗的東西 | 誰做 | agent 角色 |
|:---|:---|:---|
| GUI 互動(點按鈕、看畫面、操作流程) | 使用者,或 `/verify` | agent 不自動化 GUI |
| 執行時系統真相(action/SQL/transaction/exception/milestone) | 診斷日誌 | agent 直接讀日誌 |

關鍵的分界是這樣:**agent 不去點 GUI,但使用者點完 GUI 之後產生的日誌真相,由 agent 自己讀。** 這條界線和 jet-testing 的「禁止 WebView2 與 WinForms 的 E2E 自動化」是一致的。

## 診斷日誌是什麼

診斷日誌是第三層、僅供開發用(dev-only)的日誌。它和 `result_*` 審計表、以及 `IMessageLogStore` 裡給使用者看的 UX 訊息,三者彼此獨立。它**只在 Debug 組建下啟用**(由 `enableDevTools` 控制);在 Release 組建下整條都是 no-op,不產生任何日誌。

每一筆 `DiagnosticLogEntry` 包含:時間戳、層級、category、event 名、訊息、三個關聯 id(`correlation_id`、`transaction_id`、`project_id`)、一組結構化的 `fields`(裡頭有 SQL、參數、`duration_ms`、`rows_affected` 等等),以及 exception。其中 `correlation_id` 可以把同一個 action 跨各層的日誌串起來一起追。

## 取得日誌的兩條路徑

### 主路徑:讀診斷日誌檔(預設做法,不必手動複製)

以 Debug 組建啟動時,檔案 sink(`NdjsonFileLoggerProvider`)會把每一筆日誌即時 append 到一個固定路徑,agent 直接去讀這個檔:

```
%LOCALAPPDATA%\JET\logs\jet-dev-<啟動時間戳>.ndjson
```

流程是這樣:使用者以 Debug 啟動程式(用 VSCode F5 的 `.NET Core Launch`,或執行 `dotnet run --project src/JET/JET`),並操作要驗的那段流程。接著 agent 用 `Read` 或 `Grep` 去讀最新的那個 `.ndjson` 檔(每次啟動會產生一個檔,取時間戳最新的那個),比對行為。檔案每一行是一筆完整 JSON,所以可以直接 `Grep` `correlation_id` 或事件名。Release 組建不會產生這個檔,因為整條日誌都是 no-op。

### 備援路徑:從 DEV 面板匯出

如果需要的是當下的記憶體快照,或不方便直接取檔,就走 DEV 面板的「診斷日誌」:按「重新整理」(對應 `dev.log.export`,會回傳完整的 NDJSON),再按「複製」貼給 agent。內容和檔案完全一致,因為兩者共用 `DiagnosticNdjson` 序列化;差別只在這條路徑需要人手動傳遞一次。

## agent 讀日誌時看什麼

- 用 `correlation_id` 把焦點鎖在單一 action 的完整生命週期上,確認它跨各層的順序與結果。
- 比對 `fields` 裡的 SQL 與參數,確認規則確實是用「參數化的 set-based SQL」執行的(見 `docs/jet-guide.md` §1.5.2),而不是在 C# 裡用 LINQ 做。
- 檢查 exception 欄位與錯誤碼,對照 `docs/action-contract-manifest.md` 裡定義的 error shape。
- 凡是和金額相關的行為,對照 scaled 整數運算的不變量。金額是高風險區,見 jet-testing §7。

## 何時用 `/verify`

有些行為一定要實際操作 GUI 才確認得了,例如版面、互動、WebView2 前後端往返後的視覺結果。這類就走 `/verify` 或請使用者操作。診斷日誌補的是「操作之下系統做了什麼」,而不是「畫面長什麼樣」。

## 邊界

- 不要為了讀日誌而去自動化 GUI 點擊。
- Release 組建沒有診斷日誌,所以驗執行時行為一律用 Debug 組建。
- 檔案 sink 是個被動寫檔的開發工具。它如果失敗,必須自己把錯誤吞下來,不能影響到主程式。
