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
///   <item>host → JS error:    <c>{ "error": "..." }</c> (no <c>callId</c>) for a message the host
///     could not parse or route; the client surfaces it globally instead of leaving the
///     caller's promise to hang forever.</item>
/// </list>
/// Envelopes are (de)serialized through the source-generated <see cref="BridgeJsonContext"/>,
/// so no reflection-based serialization is used.
/// </remarks>
public sealed class BridgeHost
{
    private readonly IBridgeTransport _transport;

    // Maps a fully-qualified "Class.Method" name to its handler. A handler receives
    // the JSON args array and returns its result already serialized as UTF-8 JSON
    // (or null for a void result), keeping the bridge free of reflection and the
    // app's concrete types so it stays Native AOT safe.
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<ReadOnlyMemory<byte>?>>> _handlers
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
    /// e.g. <c>"Api.Hello"</c>. The handler returns its result as already-serialized
    /// UTF-8 JSON, or <c>null</c> for a void result.
    /// </summary>
    public void Register(string method, Func<JsonElement, CancellationToken, Task<ReadOnlyMemory<byte>?>> handler)
        => _handlers[method] = handler;

    /// <summary>
    /// Push an event to all JavaScript subscribers of <paramref name="channel"/>.
    /// <paramref name="payloadJson"/> must already be a valid JSON value.
    /// </summary>
    public Task Publish(string channel, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("event", true);
            writer.WriteString("channel", channel);
            writer.WritePropertyName("payload");
            // Embed the already-serialized payload verbatim instead of parsing and
            // re-serializing it. WriteRawValue validates that it is well-formed JSON.
            writer.WriteRawValue(payloadJson);
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
            ReadOnlyMemory<byte> json;
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
            _ = Handle(json, cancellationToken);
        }
    }

    private async Task Handle(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        // The only thing that can consume an error on the JS side is a pending call,
        // keyed by callId. So the first job is to recover the callId: every later
        // failure is then reported back to that caller. A message we can't pin to a
        // callId has nowhere to go - our own client always sends one, so this means
        // the client is broken or someone else is posting. We can't answer a caller
        // that doesn't exist, but we still send a callId-less error frame so the
        // client can surface the problem globally instead of hanging forever, and we
        // keep a host-side breadcrumb. The other calls in flight are unaffected.
        BridgeRequest? request = null;
        string? parseError = null;
        try
        {
            request = JsonSerializer.Deserialize(json.Span, BridgeJsonContext.Default.BridgeRequest);
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
        }

        if (request?.CallId is not int callId)
        {
            // We couldn't get a callId from the structured parse. If the frame was
            // malformed, try a lenient scan to recover the callId anyway so we can
            // reject the exact caller (our client always sends callId first, so a
            // corrupt args tail still leaves it readable). Only fall back to a global,
            // uncorrelated error frame when even that fails - then there is genuinely
            // no caller to tie the failure to.
            if (parseError is not null && TryRecoverCallId(json.Span) is int corruptCallId)
            {
                Console.Error.WriteLine($"Atlantis bridge: could not parse request {corruptCallId}: {parseError}");
                try { await SendError(corruptCallId, $"Atlantis bridge could not parse the request: {parseError}").ConfigureAwait(false); }
                catch { }
                return;
            }

            var reason = parseError is not null
                ? $"could not parse message: {parseError}"
                : "received a message with no callId";
            Console.Error.WriteLine($"Atlantis bridge: {reason}: {Encoding.UTF8.GetString(json.Span)}");
            // Best-effort: if the transport is gone there's nothing more we can do.
            try { await SendBridgeError($"Atlantis bridge host {reason}").ConfigureAwait(false); }
            catch { }
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

            var result = await handler(request.Args, cancellationToken).ConfigureAwait(false);
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

    // Best-effort scan to pull "callId" out of a frame the structured parse rejected, so
    // a corrupt request (typically a malformed args tail after a valid leading callId)
    // can still be reported to the exact caller instead of only as a global error.
    // Returns null if the callId can't be read before the malformed portion.
    private static int? TryRecoverCallId(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isCallId = reader.ValueTextEquals("callId");
                if (!reader.Read())
                    return null;
                if (isCallId)
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int id) ? id : null;
                reader.Skip();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private Task SendResult(int callId, ReadOnlyMemory<byte>? result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("callId", callId);
            writer.WritePropertyName("result");
            // Embed the handler's already-serialized UTF-8 result verbatim (null for void).
            if (result is null)
                writer.WriteNullValue();
            else
                writer.WriteRawValue(result.Value.Span);
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

    // A callId-less error for a message we couldn't parse or route. There's no pending
    // caller to reject, so the client surfaces this globally (see atlantis.ts).
    private Task SendBridgeError(string message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }
        return _transport.Send(buffer.WrittenMemory);
    }
}
