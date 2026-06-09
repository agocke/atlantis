using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlantis.Bridge;

/// <summary>
/// A request coming from JavaScript: a call to <c>className.methodName</c> with a
/// JSON <c>args</c> array. <see cref="CallId"/> is nullable so non-request messages
/// (which lack it) can be told apart and ignored.
/// </summary>
internal sealed class BridgeRequest
{
    public int? CallId { get; set; }
    public string? ClassName { get; set; }
    public string? MethodName { get; set; }
    public JsonElement Args { get; set; }
}

/// <summary>A successful response: <c>{ "callId": n, "result": &lt;json&gt; }</c>.</summary>
internal sealed class BridgeResponse
{
    public int CallId { get; set; }
    public JsonElement Result { get; set; }
}

/// <summary>An error response: <c>{ "callId": n, "error": "..." }</c>.</summary>
internal sealed class BridgeError
{
    public int CallId { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>A pushed event: <c>{ "event": true, "channel": "...", "payload": &lt;json&gt; }</c>.</summary>
internal sealed class BridgeEvent
{
    public bool Event { get; } = true;
    public string Channel { get; set; } = "";
    public JsonElement Payload { get; set; }
}

/// <summary>
/// Source-generated (reflection-free) serialization metadata for the bridge wire
/// envelopes, so the host stays Native AOT safe. The <see cref="JsonElement"/>
/// members carry already-parsed JSON through verbatim.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeResponse))]
[JsonSerializable(typeof(BridgeError))]
[JsonSerializable(typeof(BridgeEvent))]
internal partial class BridgeJsonContext : JsonSerializerContext;
