using System.Text.Json;

namespace Atlantis.Bridge;

/// <summary>
/// Host side of the Atlantis JS bridge. Dispatches method calls coming from the
/// webview to registered handlers and pushes events back to JavaScript.
/// </summary>
/// <remarks>
/// Wire protocol (all messages are JSON):
/// <list type="bullet">
///   <item>JS → host request: <c>{ "callId": n, "className": "Api", "methodName": "Foo", "args": [...] }</c></item>
///   <item>host → JS response: <c>{ "callId": n, "result": &lt;json&gt; }</c> or <c>{ "callId": n, "error": "..." }</c></item>
///   <item>host → JS event:    <c>{ "event": true, "channel": "...", "payload": &lt;json&gt; }</c></item>
/// </list>
/// Envelopes are (de)serialized through the source-generated <see cref="BridgeJsonContext"/>,
/// so no reflection-based serialization is used.
/// </remarks>
public sealed class BridgeHost
{
    private readonly IBridgeTransport _transport;

    // Maps "className.methodName" to its handler. A handler receives the JSON args
    // array and returns its result already serialized as a JSON string (or null for
    // a void result), keeping the bridge free of reflection and the app's concrete
    // types so it stays Native AOT safe.
    private readonly Dictionary<string, Func<JsonElement, Task<string?>>> _handlers
        = new(StringComparer.Ordinal);

    internal BridgeHost(IBridgeTransport transport)
    {
        _transport = transport;
    }

    /// <summary>Register a handler for <c>className.methodName</c>.</summary>
    public void Register(string className, string methodName, Func<JsonElement, Task<string?>> handler)
        => _handlers[Key(className, methodName)] = handler;

    /// <summary>
    /// Push an event to all JavaScript subscribers of <paramref name="channel"/>.
    /// <paramref name="payloadJson"/> must already be a valid JSON value.
    /// </summary>
    public Task Publish(string channel, string payloadJson)
    {
        var evt = new BridgeEvent { Channel = channel, Payload = Parse(payloadJson) };
        return _transport.Send(JsonSerializer.Serialize(evt, BridgeJsonContext.Default.BridgeEvent));
    }

    /// <summary>
    /// Pump messages from the transport, dispatching each request to its handler,
    /// until <paramref name="cancellationToken"/> is signalled.
    /// </summary>
    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            string json;
            try
            {
                json = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Dispatch concurrently so a slow handler doesn't stall other calls.
            // Dispatch swallows its own exceptions, so the task never faults.
            _ = Dispatch(json);
        }
    }

    private async Task Dispatch(string json)
    {
        var callId = -1;
        try
        {
            var request = JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeRequest);
            if (request?.CallId is not int id)
                return; // not a request (e.g. malformed or an unrelated message); ignore

            callId = id;
            var className = request.ClassName ?? "";
            var methodName = request.MethodName ?? "";

            if (!_handlers.TryGetValue(Key(className, methodName), out var handler))
            {
                await SendError(callId, $"No handler registered for {className}.{methodName}").ConfigureAwait(false);
                return;
            }

            var result = await handler(request.Args).ConfigureAwait(false);
            await SendResult(callId, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (callId >= 0)
            {
                // Best-effort error delivery; if the transport is gone there's nothing more to do.
                try { await SendError(callId, ex.Message).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private Task SendResult(int callId, string? resultJson)
    {
        var response = new BridgeResponse { CallId = callId, Result = Parse(resultJson) };
        return _transport.Send(JsonSerializer.Serialize(response, BridgeJsonContext.Default.BridgeResponse));
    }

    private Task SendError(int callId, string message)
    {
        var error = new BridgeError { CallId = callId, Error = message };
        return _transport.Send(JsonSerializer.Serialize(error, BridgeJsonContext.Default.BridgeError));
    }

    // Parse an already-serialized JSON fragment (or null) into a standalone JsonElement
    // that can be embedded in an envelope. Clone() detaches it from the temporary document.
    private static JsonElement Parse(string? json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "null" : json);
        return doc.RootElement.Clone();
    }

    private static string Key(string className, string methodName) => className + "." + methodName;
}
