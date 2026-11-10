using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace LuckyRobots.Piper.Host;

/// <summary>Ingress checks for a control request body, shared by HTTP and gRPC.</summary>
internal static class ControlJson
{
    internal const int MaxBytes = 16384, MaxDepth = 32;

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
