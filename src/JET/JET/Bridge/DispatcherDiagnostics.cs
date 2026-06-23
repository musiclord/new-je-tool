using Microsoft.Extensions.Logging;

namespace JET.Bridge;

/// <summary>
/// ActionDispatcher 的診斷日誌事件（LoggerMessage 來源產生器;結構化欄位即 NDJSON 欄位）。
/// correlation_id / project_id 由 dispatcher 的 BeginScope 攜帶,不在訊息模板內。
/// </summary>
internal static partial class DispatcherDiagnostics
{
    [LoggerMessage(EventId = 1000, EventName = "action.start", Level = LogLevel.Information,
        Message = "action {action} start")]
    public static partial void ActionStart(ILogger logger, string action);

    [LoggerMessage(EventId = 1001, EventName = "action.end", Level = LogLevel.Information,
        Message = "action {action} {result_status} in {duration_ms} ms")]
    public static partial void ActionEnd(ILogger logger, string action, string result_status, long duration_ms);

    [LoggerMessage(EventId = 1002, EventName = "action.error", Level = LogLevel.Error,
        Message = "action {action} failed in {duration_ms} ms")]
    public static partial void ActionError(ILogger logger, string action, long duration_ms, Exception exception);
}
