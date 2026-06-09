using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Atlantis.Bridge;

/// <summary>
/// Host side of the Atlantis JS bridge. Dispatches method calls coming from the
/// webview to registered handlers and pushes events back to JavaScript.
/// </summary>
/// <remarks>
/// Wire protocol (all messages are JSON):
/// <list type="bullet">
///   <item>JS → host request: <c>{ "callId": n, "method": "Api.Foo", "args": [...] }</c></item>
///   <item>host → JS response: <c>{ "callId": n, "result": &lt;json&gt; }</c> or <c>{ "callId": n, "error": "..." }</c></item>
///   <item>host → JS event:    <c>{ "event": true, "channel": "...", "payload": &lt;json&gt; }</c></item>
/// </list>
/// Envelopes are (de)serialized through the source-generated <see cref="BridgeJsonContext"/>,
/// so no reflection-based serialization is used.
/// </remarks>
public sealed class BridgeHost
{
    private readonly IBridgeTransport _transport;

    // Maps a fully-qualified "Class.Method" name to its handler. A handler receives
    // the JSON args array and returns its result already serialized as a JSON string
    // (or null for a void result), keeping the bridge free of reflection and the
    // app's concrete types so it stays Native AOT safe.
    private readonly Dictionary<string, Func<JsonElement, Task<string?>>> _handlers
        = new(StringComparer.Ordinal);

    internal BridgeHost(IBridgeTransport transport)
    {
        _transport = transport;
    }

    /// <summary>
    /// Attach a bridge to <paramref name="transport"/>, let the app register its
    /// handlers via <paramref name="configure"/>, and start pumping messages until
    /// <paramref name="cancellationToken"/> is signalled.
    /// </summary>
    internal static void Attach(IBridgeTransport transport, Action<BridgeHost> configure, CancellationToken cancellationToken)
    {
        var bridge = new BridgeHost(transport);
        configure(bridge);
        _ = bridge.RunAsync(cancellationToken);
    }

    /// <summary>
    /// Register a handler for the fully-qualified <paramref name="method"/> name,
    /// e.g. <c>"Api.Hello"</c>.
    /// </summary>
    public void Register(string method, Func<JsonElement, Task<string?>> handler)
        => _handlers[method] = handler;

    /// <summary>
    /// Push an event to all JavaScript subscribers of <paramref name="channel"/>.
    /// <paramref name="payloadJson"/> must already be a valid JSON value.
    /// </summary>
    public Task Publish(string channel, string payloadJson)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("event", true);
            writer.WriteString("channel", channel);
            writer.WritePropertyName("payload");
            // Embed the already-serialized payload verbatim instead of parsing and
            // re-serializing it.
            writer.WriteRawValue(string.IsNullOrEmpty(payloadJson) ? "null" : payloadJson);
            writer.WriteEndObject();
        }
        return _transport.Send(buffer.WrittenMemory);
    }

    /// <summary>
    /// Pump messages from the transport, dispatching each request to its handler,
    /// until <paramref name="cancellationToken"/> is signalled.
    /// </summary>
    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            byte[] json;
            try
            {
                json = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Handle each message independently and concurrently so one slow or bad
            // call can't stall the others. Handle never throws.
            _ = Handle(json);
        }
    }

    private async Task Handle(byte[] json)
    {
        // The only thing that can consume an error on the JS side is a pending call,
        // keyed by callId. So the first job is to recover the callId: every later
        // failure is then reported back to that caller. A message we can't pin to a
        // callId has nowhere to go - our own client always sends one, so this means
        // the client is broken or someone else is posting. Surface it and move on;
        // we can't answer a caller that doesn't exist, and the other calls are fine.
        BridgeRequest? request = null;
        try { request = JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeRequest); }
        catch (JsonException) { }

        if (request?.CallId is not int callId)
        {
            Console.Error.WriteLine($"Atlantis bridge: dropping unroutable message: {Encoding.UTF8.GetString(json)}");
            return;
        }

        try
        {
            var method = request.Method ?? "";

            if (!_handlers.TryGetValue(method, out var handler))
            {
                await SendError(callId, $"No handler registered for {method}").ConfigureAwait(false);
                return;
            }

            var result = await handler(request.Args).ConfigureAwait(false);
            await SendResult(callId, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The call failed; reject its caller's promise. The channel is healthy, so
            // this is recoverable. Best-effort: if the transport is gone, give up.
            try { await SendError(callId, ex.Message).ConfigureAwait(false); }
            catch { }
        }
    }

    private Task SendResult(int callId, string? resultJson)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("callId", callId);
            writer.WritePropertyName("result");
            // Embed the handler's already-serialized result verbatim (null for void).
            writer.WriteRawValue(string.IsNullOrEmpty(resultJson) ? "null" : resultJson);
            writer.WriteEndObject();
        }
        return _transport.Send(buffer.WrittenMemory);
    }

    private Task SendError(int callId, string message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("callId", callId);
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }
        return _transport.Send(buffer.WrittenMemory);
    }
}
