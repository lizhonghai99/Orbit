using System.Text.Json;
using Orbit.Shell.I18n;

namespace Orbit.Shell.ViewModels;

/// <summary>Maps tool names/args into Cursor-style one-line activity text.</summary>
public static class ToolActivityFormatter
{
    public static ToolCallRowVm Format(string name, string status, string argsText, string resultText)
    {
        var verb = VerbFor(name);
        var detail = DetailFor(name, argsText, status);
        return new ToolCallRowVm
        {
            Name = name,
            StatusLabel = status,
            Verb = verb,
            Detail = detail,
            ArgsText = argsText,
            ResultText = resultText
        };
    }

    public static string VerbFor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("search") || n.Contains("crawl") || n.Contains("extract"))
        {
            return Loc.T("chat.verb.searched");
        }

        if (n.Contains("read") || n.Contains("_ls") || n.Contains("grep") || n.Contains("get_"))
        {
            return Loc.T("chat.verb.read");
        }

        if (n.Contains("write") || n.Contains("replace") || n.Contains("create") || n.Contains("save"))
        {
            return Loc.T("chat.verb.edited");
        }

        if (n.Contains("delete") || n.Contains("remove") || n.Contains("clear"))
        {
            return Loc.T("chat.verb.deleted");
        }

        if (n.StartsWith("background_agents_", StringComparison.Ordinal)
            || n.Contains("bash")
            || n.Contains("run_")
            || n.Contains("pipeline")
            || n.Contains("start_task")
            || n.Contains("wait"))
        {
            return Loc.T("chat.verb.ran");
        }

        if (n.StartsWith("mode_", StringComparison.Ordinal))
        {
            return Loc.T("chat.verb.set");
        }

        return Loc.T("chat.verb.called");
    }

    public static string DetailFor(string name, string argsText, string status)
    {
        var fromArgs = TryPickArg(argsText, "query", "fileName", "path", "command", "description", "url", "mode", "agentName", "topic", "focus");
        if (!string.IsNullOrWhiteSpace(fromArgs))
        {
            var clipped = Clip(fromArgs!, 64);
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "…", StringComparison.Ordinal)
                ? clipped
                : $"{clipped} · {status}";
        }

        var shortName = name.Contains('_') ? name[(name.LastIndexOf('_') + 1)..] : name;
        return string.IsNullOrWhiteSpace(status) || status is "ok" or "…"
            ? shortName
            : $"{shortName} · {status}";
    }

    private static string? TryPickArg(string argsText, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(argsText))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (!doc.RootElement.TryGetProperty(key, out var v))
                {
                    continue;
                }

                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                }
                else if (v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    return v.ToString();
                }
            }
        }
        catch
        {
            // plain text args
        }

        return null;
    }

    private static string Clip(string text, int max)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
