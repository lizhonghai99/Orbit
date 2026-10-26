using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Contracts;

/// <summary>Structured Runtime API error body: <c>{ "code", "message", "details?" }</c>.</summary>
public sealed class OrbitApiError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = OrbitErrorCodes.BadRequest;

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Details { get; set; }

    public static OrbitApiError Create(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        new()
        {
            Code = code,
            Message = message,
            Details = details is null ? null : new Dictionary<string, string>(details)
        };

    /// <summary>
    /// Parse new <see cref="OrbitApiError"/> JSON, or legacy <c>{ "error": "…" }</c>
    /// (code inferred from known prefixes when possible).
    /// </summary>
    public static OrbitApiError? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("code", out var codeEl)
                && codeEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(codeEl.GetString()))
            {
                var code = codeEl.GetString()!.Trim();
                var message = root.TryGetProperty("message", out var msgEl)
                              && msgEl.ValueKind == JsonValueKind.String
                    ? msgEl.GetString() ?? ""
                    : code;
                Dictionary<string, string>? details = null;
                if (root.TryGetProperty("details", out var det)
                    && det.ValueKind == JsonValueKind.Object)
                {
                    details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in det.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            details[p.Name] = p.Value.GetString() ?? "";
                        }
                        else
                        {
                            details[p.Name] = p.Value.ToString();
                        }
                    }
                }

                return new OrbitApiError { Code = code, Message = message, Details = details };
            }

            if (root.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.String)
            {
                var text = err.GetString()?.Trim() ?? "";
                if (text.Length == 0)
                {
                    return null;
                }

                return new OrbitApiError
                {
                    Code = InferCodeFromLegacyMessage(text),
                    Message = text
                };
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    public static Exception ToException(string? body, string? fallback = null)
    {
        var parsed = TryParse(body);
        if (parsed is not null)
        {
            return new OrbitApiException(parsed.Code, parsed.Message, parsed.Details);
        }

        return new InvalidOperationException(
            string.IsNullOrWhiteSpace(body) ? (fallback ?? "Request failed.") : body);
    }

    private static string InferCodeFromLegacyMessage(string text)
    {
        foreach (var code in KnownPrefixes)
        {
            if (text.StartsWith(code, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }

        if (text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return OrbitErrorCodes.Unauthorized;
        }

        return OrbitErrorCodes.BadRequest;
    }

    private static readonly string[] KnownPrefixes =
    [
        OrbitErrorCodes.ConfirmRequired,
        OrbitErrorCodes.SessionBusy,
        OrbitErrorCodes.WorkspaceMismatch,
        OrbitErrorCodes.WorkspaceRequired,
        OrbitErrorCodes.WorkspaceMissing,
        OrbitErrorCodes.WorkspaceActiveLocked,
        OrbitErrorCodes.SelectionRequired,
    ];
}

/// <summary>Shell-facing exception carrying a stable <see cref="Code"/>.</summary>
public sealed class OrbitApiException : Exception
{
    public OrbitApiException(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null)
        : base(message)
    {
        Code = code;
        Details = details is null
            ? null
            : new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
    }

    public string Code { get; }

    public IReadOnlyDictionary<string, string>? Details { get; }

    public bool Is(string code) =>
        string.Equals(Code, code, StringComparison.OrdinalIgnoreCase);
}
