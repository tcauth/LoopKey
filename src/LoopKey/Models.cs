using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoopKey;

internal sealed record AuthManifest(
    string RequestId,
    string Profile,
    string AuthorizationUrl,
    int CallbackPort,
    DateTimeOffset ExpiresAt);

internal sealed record AuthStatus(string State, string Message)
{
    public bool IsTerminal => State is "succeeded" or "failed" or "expired" or "cancelled";
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
