using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace LuckyRobots.Piper.Host;

/// <summary>The one ingress validator for a control request body, shared by HTTP and gRPC.
/// Its limits are the API definition's. It checks the body's form only: the fields of a
/// core route are validated by the core against its state, in the core's own order.</summary>
internal static class ControlJson
{
    internal const int MaxBytes = PiperApi.MaxBodyBytes, MaxDepth = PiperApi.MaxDepth;

    /// <summary>Why a body was refused. HTTP replies with <see cref="Status"/>;
    /// gRPC reports the same message as InvalidArgument.</summary>
    internal readonly record struct Rejection(int Status, string Message);

    internal static bool TryParse(string body, [NotNullWhen(true)] out JsonDocument? document, out Rejection rejection)
    {
        document = null;
        rejection = default;
        if (Encoding.UTF8.GetByteCount(body) > MaxBytes) { rejection = new(413, "Request too large"); return false; }
        if (body.Contains("\\u", StringComparison.Ordinal) || body.Contains('\0'))
        {
            rejection = new(400, "Unicode escapes and NUL are unsupported in control requests");
            return false;
        }
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = MaxDepth }); }
        catch (JsonException) { rejection = new(400, "Invalid JSON body"); return false; }
        if (parsed.RootElement.ValueKind != JsonValueKind.Object || !HasUniqueKeys(parsed.RootElement))
        {
            parsed.Dispose();
            rejection = new(400, "Expected one JSON object with unique keys");
            return false;
        }
        document = parsed;
        return true;
    }

    private static bool HasUniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in element.EnumerateObject())
                if (!names.Add(member.Name) || !HasUniqueKeys(member.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) if (!HasUniqueKeys(child)) return false;
        return true;
    }
}
