using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Agents.Browser;

namespace Orbit.Agents.Mail.Mail;

/// <summary>
/// Durable mailbox registry under FileMemory
/// (<c>mailboxes.json</c> + human <c>MEMORY.md</c> / <c>mailboxes.md</c>).
/// Chromium folders are owned by <see cref="BrowserSessionService"/>; this type only maps names ↔ ids.
/// </summary>
public sealed class MailboxRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _memoryRoot;
    private readonly string _jsonPath;
    private readonly object _gate = new();

    public MailboxRegistry(string agentId, string? contentRoot = null)
    {
        _memoryRoot = ResolveMemoryRoot(agentId, contentRoot);
        Directory.CreateDirectory(_memoryRoot);
        _jsonPath = Path.Combine(_memoryRoot, "mailboxes.json");
    }

    public string MemoryRoot => _memoryRoot;
    public string JsonPath => _jsonPath;

    public MailboxRegistryState Load()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    public MailboxRegistryState EnsureDefault(string defaultProfileId = "default")
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            if (state.Mailboxes.Count == 0)
            {
                state = new MailboxRegistryState
                {
                    ActiveId = defaultProfileId,
                    AskedAboutMore = false,
                    Mailboxes =
                    [
                        new MailboxRecord
                        {
                            Id = defaultProfileId,
                            Name = "Default",
                            IsDefault = true,
                            CreatedAt = DateTimeOffset.UtcNow
                        }
                    ]
                };
                SaveUnlocked(state);
            }

            EnsureMarkdownUnlocked(state);
            return state;
        }
    }

    public MailboxRegistryState Register(string name, bool setActive, bool? isDefault = null)
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            var id = SanitizeId(name);
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Mailbox name is required.", nameof(name));
            }

            if (state.Mailboxes.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Mailbox '{id}' already exists.");
            }

            var makeDefault = isDefault ?? state.Mailboxes.Count == 0;
            if (makeDefault)
            {
                foreach (var m in state.Mailboxes)
                {
                    m.IsDefault = false;
                }
            }

            state.Mailboxes.Add(new MailboxRecord
            {
                Id = id,
                Name = name.Trim(),
                IsDefault = makeDefault,
                CreatedAt = DateTimeOffset.UtcNow
            });

            if (setActive || state.Mailboxes.Count == 1)
            {
                state.ActiveId = id;
            }

            SaveUnlocked(state);
            return state;
        }
    }

    public MailboxRegistryState SetActive(string idOrName)
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            var match = Find(state, idOrName)
                        ?? throw new KeyNotFoundException($"Mailbox '{idOrName}' not found.");
            state.ActiveId = match.Id;
            SaveUnlocked(state);
            return state;
        }
    }

    public MailboxRegistryState MarkAskedAboutMore(bool asked = true)
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            state.AskedAboutMore = asked;
            SaveUnlocked(state);
            return state;
        }
    }

    public MailboxRecord? Find(MailboxRegistryState state, string idOrName)
    {
        var key = idOrName.Trim();
        return state.Mailboxes.FirstOrDefault(m =>
                   string.Equals(m.Id, key, StringComparison.OrdinalIgnoreCase))
               ?? state.Mailboxes.FirstOrDefault(m =>
                   string.Equals(m.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mailbox id slug; delegates to shared browser profile id rules (disk-compatible).</summary>
    public static string SanitizeId(string name) => BrowserProfileIds.Sanitize(name);

    public static string ResolveMemoryRoot(string agentId, string? contentRoot)
    {
        var root = Orbit.Contracts.OrbitPathNames.ResolveDataRoot(contentRoot);
        return Path.GetFullPath(
            Path.Combine(root, "data", "file-memory", Orbit.Contracts.OrbitPathNames.SanitizeFileName(agentId)));
    }

    private MailboxRegistryState LoadUnlocked()
    {
        if (!File.Exists(_jsonPath))
        {
            return new MailboxRegistryState();
        }

        try
        {
            var json = File.ReadAllText(_jsonPath);
            return JsonSerializer.Deserialize<MailboxRegistryState>(json, JsonOptions)
                   ?? new MailboxRegistryState();
        }
        catch (JsonException)
        {
            try
            {
                var bak = _jsonPath + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(_jsonPath, bak, overwrite: true);
            }
            catch
            {
                // best-effort
            }

            throw new InvalidDataException(
                $"Mailbox registry JSON is corrupt: {_jsonPath}. A .corrupt.* backup was attempted.");
        }
    }

    private void SaveUnlocked(MailboxRegistryState state)
    {
        if (string.IsNullOrWhiteSpace(state.ActiveId) && state.Mailboxes.Count > 0)
        {
            state.ActiveId = state.Mailboxes.FirstOrDefault(m => m.IsDefault)?.Id
                             ?? state.Mailboxes[0].Id;
        }

        var dir = Path.GetDirectoryName(_jsonPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = _jsonPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tmp, _jsonPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // best-effort
            }
        }

        EnsureMarkdownUnlocked(state);
    }

    private void EnsureMarkdownUnlocked(MailboxRegistryState state)
    {
        var rows = state.Mailboxes.Select(m =>
        {
            var active = string.Equals(m.Id, state.ActiveId, StringComparison.OrdinalIgnoreCase)
                ? "yes"
                : "";
            var def = m.IsDefault ? "yes" : "";
            return $"| `{m.Id}` | {EscapeCell(m.Name)} | {def} | {active} |";
        });

        var md = $"""
            # Mailboxes

            Active: `{state.ActiveId ?? "(none)"}`  
            Asked about more accounts: {(state.AskedAboutMore ? "yes" : "no")}

            | id | name | default | active |
            |----|------|---------|--------|
            {string.Join('\n', rows)}

            Profile folders live under data/browser-profiles/<agent>/<id>/.
            Use tools list_mailboxes / register_mailbox / switch_mailbox — do not edit this file by hand unless necessary.
            """;

        AtomicWriteText(Path.Combine(_memoryRoot, "mailboxes.md"), md.Replace("\r\n", "\n"));

        var memoryPath = Path.Combine(_memoryRoot, "MEMORY.md");
        if (!File.Exists(memoryPath))
        {
            AtomicWriteText(memoryPath, """
                # MEMORY

                Durable Orbit Mail notes (FileMemory convention).

                - `mailboxes.md` / `mailboxes.json` — named Outlook profiles (default / work / …)
                - `inbox-*-status.md` — last inbox inventory per mailbox (prefer `inbox-{id}-status.md`)
                - Prefer tools over guessing; keep mailbox-scoped files when the user has more than one account.
                """.Replace("\r\n", "\n"));
        }
    }

    private static void AtomicWriteText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static string EscapeCell(string value) =>
        value.Replace("|", "/", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

public sealed class MailboxRegistryState
{
    public string? ActiveId { get; set; }
    public bool AskedAboutMore { get; set; }
    public List<MailboxRecord> Mailboxes { get; set; } = [];
}

public sealed class MailboxRecord
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
