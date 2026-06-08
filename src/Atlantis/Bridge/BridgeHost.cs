using System.Text;
using System.Text.Json;

namespace Atlantis.Bridge;

/// <summary>
/// A handler for an exported method. Receives the JSON <c>args</c> array sent
/// from JavaScript and returns the result already serialized as a JSON string
/// (or <c>null</c> for a void result). Keeping results pre-serialized lets the
/// bridge stay free of any reflection and of the app's concrete types, so it
/// remains Native AOT safe.
/// </summary>
public delegate ValueTask<string?> BridgeHandler(JsonElement args, CancellationToken ct);

/// <summary>
/// Host side of the Atlantis JS bridge. Dispatches method calls coming from the
/// webview to registered <see cref="BridgeHandler"/>s and pushes events back to
/// JavaScript.
/// </summary>
/// <remarks>
/// Wire protocol (all messages are JSON):
/// <list type="bullet">
///   <item>JS → host request: <c>{ "callId": n, "className": "Api", "methodName": "Foo", "args": [...] }</c></item>
///   <item>host → JS response: <c>{ "callId": n, "result": &lt;json&gt; }</c> or <c>{ "callId": n, "error": "..." }</c></item>
///   <item>host → JS event:    <c>{ "event": true, "channel": "...", "payload": &lt;json&gt; }</c></item>
/// </list>
/// </remarks>
public sealed class BridgeHost
{
    private readonly IBridgeTransport _transport;
    private readonly Dictionary<string, BridgeHandler> _handlers = new(StringComparer.Ordinal);

    public BridgeHost(IBridgeTransport transport)
    {
        _transport = transport;
        _transport.MessageReceived += OnMessageReceived;
    }

    /// <summary>Register a handler for <c>className.methodName</c>.</summary>
    public void Register(string className, string methodName, BridgeHandler handler)
        => _handlers[Key(className, methodName)] = handler;

    /// <summary>
    /// Push an event to all JavaScript subscribers of <paramref name="channel"/>.
    /// <paramref name="payloadJson"/> must already be a valid JSON value.
    /// </summary>
    public void Publish(string channel, string payloadJson)
    {
        var sb = new StringBuilder(payloadJson.Length + channel.Length + 32);
        sb.Append("{\"event\":true,\"channel\":");
        AppendJsonString(sb, channel);
        sb.Append(",\"payload\":").Append(payloadJson).Append('}');
        _transport.Send(sb.ToString());
    }

    private async void OnMessageReceived(string json)
    {
        var callId = -1;
        try
        {
            string className;
            string methodName;
            JsonElement args;

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("callId", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    return; // not a request (e.g. malformed); ignore

                callId = idEl.GetInt32();
                className = root.TryGetProperty("className", out var c) ? c.GetString() ?? "" : "";
                methodName = root.TryGetProperty("methodName", out var m) ? m.GetString() ?? "" : "";
                args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
            }

            if (!_handlers.TryGetValue(Key(className, methodName), out var handler))
            {
                SendError(callId, $"No handler registered for {className}.{methodName}");
                return;
            }

            var result = await handler(args, CancellationToken.None).ConfigureAwait(false);
            SendResult(callId, result);
        }
        catch (Exception ex)
        {
            if (callId >= 0)
                SendError(callId, ex.Message);
        }
    }

    private void SendResult(int callId, string? resultJson)
    {
        var sb = new StringBuilder();
        sb.Append("{\"callId\":").Append(callId)
          .Append(",\"result\":").Append(string.IsNullOrEmpty(resultJson) ? "null" : resultJson)
          .Append('}');
        _transport.Send(sb.ToString());
    }

    private void SendError(int callId, string message)
    {
        var sb = new StringBuilder();
        sb.Append("{\"callId\":").Append(callId).Append(",\"error\":");
        AppendJsonString(sb, message);
        sb.Append('}');
        _transport.Send(sb.ToString());
    }

    private static string Key(string className, string methodName) => className + "." + methodName;

    private static void AppendJsonString(StringBuilder sb, string value)
        => sb.Append('"').Append(JsonEncodedText.Encode(value)).Append('"');
}
