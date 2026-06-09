using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlantis.Bridge;

/// <summary>
/// A request coming from JavaScript: a call to the fully-qualified <c>Method</c>
/// (<c>"ClassName.MethodName"</c>) with a JSON <c>args</c> array. <see cref="CallId"/>
/// is nullable so non-request messages (which lack it) can be told apart and ignored.
/// </summary>
internal sealed class BridgeRequest
{
    public int? CallId { get; set; }
    public string? Method { get; set; }
    public JsonElement Args { get; set; }
}

/// <summary>
/// Source-generated (reflection-free) deserialization metadata for the inbound bridge
/// request, so the host stays Native AOT safe. The outbound envelopes (response, error,
/// event) are written directly with <see cref="System.Text.Json.Utf8JsonWriter"/> so the
/// already-serialized result/payload can be embedded verbatim.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BridgeRequest))]
internal partial class BridgeJsonContext : JsonSerializerContext;
