using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using JET.Application;
using Microsoft.Extensions.Logging;

namespace JET.Bridge;

public sealed class ActionDispatcher
{
    private readonly IReadOnlyDictionary<string, IApplicationActionHandler> _handlers;
    private readonly ILogger<ActionDispatcher> _logger;
    private readonly IProjectSession _session;

    public ActionDispatcher(
        IEnumerable<IApplicationActionHandler> handlers,
        ILogger<ActionDispatcher> logger,
        IProjectSession session)
    {
        var map = new Dictionary<string, IApplicationActionHandler>(StringComparer.Ordinal);

        foreach (var handler in handlers)
        {
            if (!map.TryAdd(handler.Action, handler))
            {
                throw new InvalidOperationException($"Duplicate JET action handler: {handler.Action}");
            }
        }

        _handlers = new ReadOnlyDictionary<string, IApplicationActionHandler>(map);
        _logger = logger;
        _session = session;
    }

    public IReadOnlyCollection<string> RegisteredActions => _handlers.Keys.ToArray();

    /// <summary>
    /// 每次 dispatch 生成 correlation_id 並以 <see cref="ILogger.BeginScope"/> 建立 scope——
    /// 同一 LoggerFactory 的子層 logger（Handler/Repository）在該 async 流程內自動帶入（AsyncLocal）。
    /// 記錄 action 生命週期（start/end/error、duration_ms、result_status）;診斷日誌為 dev-only,
    /// Release 用 no-op logger。
    /// </summary>
    public async Task<object?> DispatchAsync(string action, JsonElement payload, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = Guid.NewGuid().ToString("N"),
            ["action"] = action,
            ["project_id"] = _session.CurrentProjectId,
        });

        var stopwatch = Stopwatch.StartNew();
        DispatcherDiagnostics.ActionStart(_logger, action);

        try
        {
            if (!_handlers.TryGetValue(action, out var handler))
            {
                throw new KeyNotFoundException($"No JET action handler is registered for '{action}'.");
            }

            var result = await handler.HandleAsync(payload, cancellationToken).ConfigureAwait(false);
            DispatcherDiagnostics.ActionEnd(_logger, action, "ok", stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception exception)
        {
            DispatcherDiagnostics.ActionError(_logger, action, stopwatch.ElapsedMilliseconds, exception);
            throw;
        }
    }
}
