using System.Text.Json;
using JET.Application;
using JET.Domain;
using Microsoft.Web.WebView2.Core;

namespace JET.Bridge;

public sealed class JetWebMessageBridge(CoreWebView2 webView, ActionDispatcher dispatcher)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private bool _attached;

    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        webView.WebMessageReceived += OnWebMessageReceived;
        _attached = true;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JetRequestEnvelope? request = null;
        var requestId = string.Empty;

        try
        {
            request = JsonSerializer.Deserialize<JetRequestEnvelope>(e.WebMessageAsJson, JsonOptions);

            if (request is null)
            {
                throw new InvalidOperationException("JET bridge request is empty.");
            }

            requestId = request.RequestId ?? string.Empty;

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                throw new InvalidOperationException("JET bridge requestId is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Action))
            {
                throw new InvalidOperationException("JET bridge action is required.");
            }

            var data = await dispatcher.DispatchAsync(request.Action, request.Payload, CancellationToken.None);
            Post(new JetResponseEnvelope(request.RequestId, true, data, null));
        }
        catch (Exception ex)
        {
            Post(new JetResponseEnvelope(requestId, false, null, ToErrorDto(ex)));
        }
    }

    /// <summary>JetActionException 的 code 直接上 wire；其餘例外一律 bridge_error。</summary>
    public static JetErrorDto ToErrorDto(Exception exception)
    {
        return exception is JetActionException actionException
            ? new JetErrorDto(actionException.Code, actionException.Message)
            : new JetErrorDto("bridge_error", exception.Message);
    }

    private void Post(JetResponseEnvelope response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        webView.PostWebMessageAsJson(json);
    }
}

internal sealed record JetRequestEnvelope(
    string? RequestId,
    string? Action,
    JsonElement Payload);

internal sealed record JetResponseEnvelope(
    string RequestId,
    bool Ok,
    object? Data,
    JetErrorDto? Error);

public sealed record JetErrorDto(
    string Code,
    string Message);
