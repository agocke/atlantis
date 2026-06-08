using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Atlantis.Bridge;

/// <summary>
/// Helpers for reading the JSON <c>args</c> array passed to a
/// <see cref="BridgeHandler"/>. Uses <see cref="JsonTypeInfo{T}"/> so callers
/// stay Native AOT safe (no reflection-based serialization).
/// </summary>
public static class BridgeArgs
{
    /// <summary>Deserialize the argument at <paramref name="index"/>.</summary>
    public static T? Get<T>(JsonElement args, int index, JsonTypeInfo<T> type)
    {
        if (args.ValueKind != JsonValueKind.Array || index >= args.GetArrayLength())
            return default;
        return args[index].Deserialize(type);
    }

    /// <summary>Read a string argument at <paramref name="index"/>.</summary>
    public static string? GetString(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || index >= args.GetArrayLength())
            return null;
        var el = args[index];
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
