using System.Text.RegularExpressions;

namespace Orbit.Agents.Coder.Coder;

/// <summary>
/// Deny-by-default command policy for Coder <c>bash</c>: allowlisted executables,
/// blocked escape patterns, and absolute paths must stay under the workspace root.
/// First-layer guard only — pair with Job Object + secret-free env; not a full sandbox.
/// </summary>
public static partial class CoderBashPolicy
{
    /// <summary>First-token allowlist (basename, no extension).</summary>
    public static IReadOnlySet<string> AllowedExecutables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // build / test
        "dotnet", "msbuild", "nuget", "xunit", "vstest.console",
        // vcs
        "git",
        // node (common in polyglot workspaces)
        "npm", "npx", "node", "pnpm", "yarn",
        // python
        "python", "python3", "py", "pip", "pip3",
        // other toolchains
        "cargo", "rustc", "go", "cmake", "make",
        // filesystem / inspect (workspace-relative)
        "ls", "dir", "pwd", "cd", "cat", "type", "echo", "printf",
        "mkdir", "md", "touch", "cp", "copy", "mv", "move", "rm", "del", "rd", "rmdir",
        "head", "tail", "wc", "sort", "uniq", "find", "findstr", "rg", "grep",
        "where", "which", "tree", "stat", "file",
        // helpers
        "true", "false", "test", "env", "printenv", "uname", "date", "sleep",
        "diff", "fc", "comp"
    };

    private static readonly string[] BlockedSubstrings =
    [
        "rm -rf /", "sudo ", "shutdown", "reboot", "> /dev/",
        "Format-Volume", "Remove-Item -Recurse -Force C:\\",
        "del /s /q C:\\", "rd /s /q C:\\",
        "Invoke-WebRequest", "Invoke-RestMethod", "Start-Process",
        "DownloadString", "FromBase64String", "Add-Type",
        "/etc/passwd", "/etc/shadow", "\\\\.\\",
        // expansion / home escapes (bash -lc still expands inside the script string)
        "$(", "`", "${",
        "$HOME", "$USERPROFILE", "$APPDATA", "$LOCALAPPDATA",
        // PowerShell-scoped env syntax; matched case-insensitively so $ENV:/$Env: are covered.
        "$env:",
        "%APPDATA%", "%LOCALAPPDATA%", "%USERPROFILE%", "%HOMEPATH%", "%HOMEDRIVE%",
        // find/xargs execution + bash process substitution
        "-exec", "-execdir", "<(", ">("
    ];

    /// <summary>Nested shells / interpreters that defeat the allowlist.</summary>
    private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "zsh", "fish", "cmd", "cmd.exe",
        "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "wscript", "cscript", "mshta", "certutil", "bitsadmin",
        "reg", "regedit", "net", "net1", "sc", "schtasks",
        "curl", "wget", "ftp", "tftp", "nc", "ncat", "ssh", "scp", "sftp"
    };

    public static bool TryValidate(string command, string workspaceRoot, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(command))
        {
            error = "command is required.";
            return false;
        }

        if (command.Length > 8_000)
        {
            error = "command exceeds 8000 characters.";
            return false;
        }

        foreach (var bad in BlockedSubstrings)
        {
            if (command.Contains(bad, StringComparison.OrdinalIgnoreCase))
            {
                error = $"blocked pattern: {bad.Trim()}";
                return false;
            }
        }

        if (ParentDirSegmentRegex().IsMatch(command))
        {
            error = "blocked path segment: ..";
            return false;
        }

        if (HomeTildeRegex().IsMatch(command))
        {
            error = "blocked home path (~).";
            return false;
        }

        var root = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var segment in SplitPipelineSegments(command))
        {
            var exe = ExtractExecutable(segment);
            if (string.IsNullOrWhiteSpace(exe))
            {
                continue;
            }

            var leaf = Path.GetFileNameWithoutExtension(exe.Trim('"', '\''));
            if (BlockedExecutables.Contains(leaf) || BlockedExecutables.Contains(exe))
            {
                error = $"executable '{leaf}' is not allowed in Coder bash.";
                return false;
            }

            if (!AllowedExecutables.Contains(leaf))
            {
                error =
                    $"executable '{leaf}' is not on the Coder bash allowlist. "
                    + "Use file_access_* for file I/O; allowlist covers git/dotnet/npm/python and common inspect commands.";
                return false;
            }

            // env curl … / env foo=bar curl — first token is allowlisted env; still scan the rest.
            if (HasBlockedSecondaryExecutable(segment, leaf, out var smuggled))
            {
                error = $"executable '{smuggled}' is not allowed after '{leaf}'.";
                return false;
            }
        }

        foreach (Match m in AbsolutePathRegex().Matches(command))
        {
            var raw = m.Value.Trim('"', '\'');
            if (!TryGetFullPath(raw, out var full))
            {
                continue;
            }

            if (!IsUnderRoot(full, root))
            {
                error = $"path '{raw}' is outside workspace '{root}'.";
                return false;
            }
        }

        return true;
    }

    /// <summary>Split on shell operators while keeping quoted spans intact (best-effort).</summary>
    public static IReadOnlyList<string> SplitPipelineSegments(string command)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                sb.Append(c);
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                sb.Append(c);
                continue;
            }

            if (!inSingle && !inDouble)
            {
                // &&  ||  ;  |  \n
                if (c is ';' or '\n' or '\r')
                {
                    Flush(list, sb);
                    continue;
                }

                if (c == '&' && i + 1 < command.Length && command[i + 1] == '&')
                {
                    Flush(list, sb);
                    i++;
                    continue;
                }

                if (c == '|' && i + 1 < command.Length && command[i + 1] == '|')
                {
                    Flush(list, sb);
                    i++;
                    continue;
                }

                if (c == '|')
                {
                    Flush(list, sb);
                    continue;
                }
            }

            sb.Append(c);
        }

        Flush(list, sb);
        return list;
    }

    public static string? ExtractExecutable(string segment)
    {
        var s = segment.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        // env VAR=x cmd → skip assignments
        while (EnvAssignRegex().IsMatch(s))
        {
            s = EnvAssignRegex().Replace(s, "", 1).TrimStart();
        }

        if (s.StartsWith('('))
        {
            s = s.TrimStart('(').Trim();
        }

        if (s.Length == 0)
        {
            return null;
        }

        if (s[0] is '"' or '\'')
        {
            var q = s[0];
            var end = s.IndexOf(q, 1);
            if (end > 1)
            {
                return s[1..end];
            }
        }

        var span = s.AsSpan();
        var i = 0;
        while (i < span.Length && !char.IsWhiteSpace(span[i]))
        {
            i++;
        }

        return s[..i];
    }

    /// <summary>
    /// After an allowlisted wrapper (<c>env</c>, <c>nice</c>, …), detect a blocked command in the remainder.
    /// </summary>
    private static bool HasBlockedSecondaryExecutable(string segment, string primaryLeaf, out string? blocked)
    {
        blocked = null;
        if (!string.Equals(primaryLeaf, "env", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(primaryLeaf, "nice", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(primaryLeaf, "nohup", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(primaryLeaf, "time", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = segment.Trim();
        // Drop the primary token (quoted or bare).
        if (rest.Length > 0 && rest[0] is '"' or '\'')
        {
            var q = rest[0];
            var end = rest.IndexOf(q, 1);
            rest = end > 0 ? rest[(end + 1)..].TrimStart() : rest;
        }
        else
        {
            var sp = rest.AsSpan();
            var i = 0;
            while (i < sp.Length && !char.IsWhiteSpace(sp[i]))
            {
                i++;
            }

            rest = i < rest.Length ? rest[i..].TrimStart() : "";
        }

        while (EnvAssignRegex().IsMatch(rest))
        {
            rest = EnvAssignRegex().Replace(rest, "", 1).TrimStart();
        }

        var secondary = ExtractExecutable(rest);
        if (string.IsNullOrWhiteSpace(secondary))
        {
            return false;
        }

        var leaf = Path.GetFileNameWithoutExtension(secondary.Trim('"', '\''));
        if (BlockedExecutables.Contains(leaf) || BlockedExecutables.Contains(secondary)
            || !AllowedExecutables.Contains(leaf))
        {
            blocked = leaf;
            return true;
        }

        return false;
    }

    /// <summary>Delegates to <see cref="Orbit.Contracts.OrbitPathNames.IsUnderOrEqual"/>.</summary>
    public static bool IsUnderRoot(string fullPath, string root) =>
        Orbit.Contracts.OrbitPathNames.IsUnderOrEqual(fullPath, root);

    private static bool TryGetFullPath(string raw, out string full)
    {
        full = "";
        try
        {
            // Skip URL-like and git refs
            if (raw.Contains("://", StringComparison.Ordinal) || raw.StartsWith("git@", StringComparison.Ordinal))
            {
                return false;
            }

            full = Path.GetFullPath(raw);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Flush(List<string> list, System.Text.StringBuilder sb)
    {
        var part = sb.ToString().Trim();
        sb.Clear();
        if (part.Length > 0)
        {
            list.Add(part);
        }
    }

    /// <summary>
    /// Windows drive, UNC, Git-Bash /c/..., and Unix absolute (/tmp, /etc/...).
    /// Unix forms use a left lookbehind so relative paths like <c>src/Orbit.Foo</c> are not matched.
    /// </summary>
    [GeneratedRegex(
        @"(""[A-Za-z]:\\[^""]*""|'[A-Za-z]:\\[^']*'|[A-Za-z]:\\[^\s|&;]+|\\\\[^\s|&;]+|(?<=^|[\s=""'])/[a-zA-Z]/[^\s|&;]+|(?<=^|[\s=""'])/[^\s|&;'""]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=(?:""[^""]*""|'[^']*'|[^\s]+)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex EnvAssignRegex();

    /// <summary><c>..</c> as a path segment (not <c>file..</c> substrings).</summary>
    [GeneratedRegex(@"(^|[\s/\\'""=])\.\.($|[\s/\\'""|&;])", RegexOptions.CultureInvariant)]
    private static partial Regex ParentDirSegmentRegex();

    [GeneratedRegex(@"(^|[\s=])~(/|\\|\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex HomeTildeRegex();
}
