using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

/// <summary>
/// Handler 測試用 host：以真 ActionDispatcher + temp projects root 組裝完整管線。
/// PublishedEvents 記錄 handler 經 IJetEventPublisher 發出的事件（host→web 推播）。
/// </summary>
internal sealed class HandlerTestHost : IDisposable
{
    private readonly TempProjectRoot _root;
    private readonly RecordingEventPublisher _events = new();
    private readonly RingBufferLoggerProvider? _diagnostic;

    public HandlerTestHost(
        IHostShell? hostShell = null,
        bool enableDevTools = true,
        string? sqlServerConnectionString = null,
        bool withDiagnosticLogFile = false)
    {
        _root = new TempProjectRoot();
        _diagnostic = enableDevTools ? new RingBufferLoggerProvider(capacity: 10_000) : null;
        DiagnosticLogDirectory = withDiagnosticLogFile ? Path.Combine(_root.Path, "logs") : null;
        Dispatcher = AppCompositionRoot.CreateDispatcher(
            hostShell ?? new StubHostShell(),
            _root.Path,
            enableDevTools,
            _events,
            sqlServerConnectionString,
            _diagnostic,
            DiagnosticLogDirectory);
    }

    public ActionDispatcher Dispatcher { get; }

    /// <summary>診斷日誌檔案 sink 的目錄(withDiagnosticLogFile=true 時於 temp root 下;否則 null)。</summary>
    public string? DiagnosticLogDirectory { get; }

    /// <summary>診斷日誌（dev-only;enableDevTools=false 時為 null）。供測試讀取 ring buffer。</summary>
    public IDiagnosticLogStore? DiagnosticLog => _diagnostic;

    public string ProjectsRoot => _root.Path;

    public IReadOnlyList<(string EventName, object Payload)> PublishedEvents => _events.Published;

    /// <summary>手寫 recording stub（jet-testing skill §1：host boundary 不用 mock framework）。</summary>
    private sealed class RecordingEventPublisher : IJetEventPublisher
    {
        public List<(string EventName, object Payload)> Published { get; } = [];

        public void Publish(string eventName, object payload)
        {
            Published.Add((eventName, payload));
        }
    }

    public async Task<JsonElement> DispatchAsync(string action, string payloadJson = "{}")
    {
        using var payload = JsonDocument.Parse(payloadJson);
        var data = await Dispatcher.DispatchAsync(action, payload.RootElement, CancellationToken.None);

        // 把匿名物件 response 轉成 JsonElement，斷言時走 wire shape
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public void Dispose() => _root.Dispose();

    private sealed class StubHostShell : IHostShell
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken)
        {
            // 預設取消(無 GUI);需驗證存檔路徑流程的測試另以 recording stub 注入。
            return Task.FromResult<string?>(null);
        }

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
        {
            // 測試環境不開檔案總管;host.openFolder 的委派驗證走 recording stub。
            return Task.CompletedTask;
        }

        public void RequestExit()
        {
            // 測試環境沒有視窗可關閉；host.exitApp 的呼叫驗證走 recording stub。
        }
    }
}
