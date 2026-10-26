using System.Collections.Concurrent;
using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;

namespace Orbit.Runtime.Sessions;

/// <summary>JSONL + meta.json session persistence (implements <see cref="ISessionStore"/>).</summary>
public sealed class JsonlSessionStore(RepoPaths paths) : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const int MaxLiveSessions = 32;

    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hot-path cache so Append does not re-read the full JSONL every turn.</summary>
    private readonly ConcurrentDictionary<string, SessionDetail> _live = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last resolved <c>?after=</c> cursor index per session (SSE idle polls hit the same cursor).</summary>
    private readonly ConcurrentDictionary<string, (string EntryId, int Index)> _syncCursor =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>id → index for the current <see cref="_live"/> entry list (O(1) GetSync cursor lookup).</summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, int>> _liveIdIndex =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Refcount of SSE (or other) pins — pinned sessions are not evicted from <see cref="_live"/>.</summary>
    private readonly ConcurrentDictionary<string, int> _livePins = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SessionSummary> List(bool includeArchived = false)
    {
        if (!Directory.Exists(paths.SessionsDir))
        {
            return [];
        }

        var list = new List<SessionSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var metaFile in Directory.EnumerateFiles(paths.SessionsDir, "*.meta.json"))
        {
            try
            {
                var header = ReadMetaFile(metaFile);
                if (header is null)
                {
                    continue;
                }

                seen.Add(header.SessionId);
                AddSummary(list, header, includeArchived);
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
        }

        // Legacy sessions: only JSONL (header embedded) until first write migrates to .meta.json.
        foreach (var file in Directory.EnumerateFiles(paths.SessionsDir, "*.jsonl"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (seen.Contains(id))
            {
                continue;
            }

            try
            {
                // List must not repair while another thread may be appending —
                // skip trailing-line repair (tolerate a partial last line as unread).
                var detail = ReadFile(file, repair: false);
                if (detail is null)
                {
                    continue;
                }

                seen.Add(detail.Header.SessionId);
                var header = WithHeader(
                    detail.Header,
                    entryCount: detail.Entries.Count);
                AddSummary(list, header, includeArchived);
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
        }

        return list.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    private static void AddSummary(List<SessionSummary> list, SessionHeader header, bool includeArchived)
    {
        var archived = header.ArchivedAt is not null;
        if (archived && !includeArchived)
        {
            return;
        }

        list.Add(new SessionSummary
        {
            SessionId = header.SessionId,
            AgentId = header.AgentId,
            Title = header.Title,
            CreatedAt = header.CreatedAt,
            UpdatedAt = header.UpdatedAt,
            EntryCount = header.EntryCount,
            IsArchived = archived,
            Summary = header.Summary,
            WorkspacePath = header.WorkspacePath
        });
    }

    public SessionDetail? Get(string sessionId)
    {
        if (!SessionIdRules.IsValid(sessionId))
        {
            return null;
        }

        lock (Gate(sessionId))
        {
            try
            {
                return Snapshot(ReadExisting(sessionId));
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }
    }

    public SessionSyncResponse? GetSync(string sessionId, string? afterEntryId)
    {
        var detail = Get(sessionId);
        if (detail is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(afterEntryId))
        {
            return new SessionSyncResponse
            {
                Header = detail.Header,
                Entries = detail.Entries,
                FullSnapshot = true
            };
        }

        var index = ResolveCursorIndex(sessionId, detail.Entries, afterEntryId);
        if (index < 0)
        {
            // Client cursor is stale (trimmed/corrupt) — force full replace.
            return new SessionSyncResponse
            {
                Header = detail.Header,
                Entries = detail.Entries,
                FullSnapshot = true
            };
        }

        var delta = detail.Entries.Skip(index + 1).ToList();
        return new SessionSyncResponse
        {
            Header = detail.Header,
            Entries = delta,
            FullSnapshot = false
        };
    }

    public SessionDetail Create(CreateSessionRequest request)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        lock (Gate(sessionId))
        {
            var now = DateTimeOffset.UtcNow;
            var header = new SessionHeader
            {
                SessionId = sessionId,
                AgentId = request.AgentId,
                Title = string.IsNullOrWhiteSpace(request.Title) ? "New chat" : request.Title.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
                CurrentEntryId = null,
                ArchivedAt = null,
                Summary = null,
                WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? null : request.WorkspaceId.Trim(),
                WorkspacePath = string.IsNullOrWhiteSpace(request.WorkspacePath) ? null : request.WorkspacePath.Trim()
            };

            var created = SessionEvents.Meta(SessionMetaKinds.SessionCreated);
            var meta = new SessionEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                ParentId = null,
                Type = created.Type,
                Timestamp = now,
                Payload = created.Payload
            };

            var writable = WithHeader(header, updatedAt: now, currentEntryId: meta.Id, entryCount: 1);
            // Entries first, then meta — crash between them leaves a low count (benign), never a high one.
            AppendEntryLine(writable.SessionId, meta);
            WriteMeta(writable);
            var detail = new SessionDetail { Header = writable, Entries = new List<SessionEntry> { meta } };
            RememberLive(sessionId, detail);
            return Snapshot(detail);
        }
    }

    /// <summary>
    /// Close any session whose last AG-UI run never got <c>agui_run_finished</c>
    /// (process kill, Runtime restart, agent crash). Idempotent.
    /// </summary>
    /// <returns>Number of sessions that received a <c>run_interrupted</c> meta entry.</returns>
    public int RecoverInterruptedRuns(string? agentIdFilter, string reason)
    {
        if (!Directory.Exists(paths.SessionsDir))
        {
            return 0;
        }

        var closed = 0;
        foreach (var file in Directory.EnumerateFiles(paths.SessionsDir, "*.jsonl"))
        {
            try
            {
                SessionDetail? detail;
                lock (Gate(Path.GetFileNameWithoutExtension(file)))
                {
                    detail = ReadFile(file);
                }

                if (detail is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(agentIdFilter)
                    && !string.Equals(detail.Header.AgentId, agentIdFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!HasOpenAguiRun(detail.Entries))
                {
                    continue;
                }

                Append(detail.Header.SessionId, SessionEvents.Meta(
                    SessionMetaKinds.RunInterrupted,
                    new
                    {
                        reason,
                        agentId = detail.Header.AgentId,
                        at = DateTimeOffset.UtcNow
                    }));
                closed++;
            }
            catch (Exception)
            {
                // Skip locked/corrupt files; next startup can retry.
            }
        }

        return closed;
    }

    internal static bool HasOpenAguiRun(IReadOnlyList<SessionEntry> entries)
    {
        var open = false;
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase)
                || entry.Payload is not JsonElement payload
                || payload.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var kind = SessionPayloadJson.ReadString(payload, "kind");
            if (string.Equals(kind, "agui_run_started", StringComparison.OrdinalIgnoreCase))
            {
                open = true;
            }
            else if (string.Equals(kind, "agui_run_finished", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(kind, "run_interrupted", StringComparison.OrdinalIgnoreCase))
            {
                open = false;
            }
        }

        return open;
    }

    public SessionDetail Append(string sessionId, AppendEntryRequest request)
    {
        SessionIdRules.RequireValid(sessionId);
        SessionEvents.Validate(request);

        lock (Gate(sessionId))
        {
            var existing = ReadExisting(sessionId);
            var parentId = request.ParentId ?? existing.Header.CurrentEntryId;
            var entry = new SessionEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                ParentId = parentId,
                Type = request.Type,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = request.Payload
            };

            var title = existing.Header.Title;
            var userCount = existing.Entries.Count(e =>
                string.Equals(e.Type, "user", StringComparison.OrdinalIgnoreCase));
            if (string.Equals(entry.Type, "user", StringComparison.OrdinalIgnoreCase) && userCount == 0)
            {
                title = TitleFromUserMessage(request.Payload) ?? title;
            }

            // Count is derived from the in-memory list — never trust a stale meta EntryCount.
            var entries = existing.Entries as List<SessionEntry> ?? existing.Entries.ToList();
            entries.Add(entry);
            var titleChanged = !string.Equals(title, existing.Header.Title, StringComparison.Ordinal);
            var header = WithHeader(
                existing.Header,
                title: title,
                updatedAt: entry.Timestamp,
                currentEntryId: entry.Id,
                entryCount: entries.Count);

            // Append-only JSONL; rewrite meta only when title (etc.) changes — EntryCount/currentEntryId
            // are recovered from JSONL on load.
            EnsureMigratedJsonl(existing);
            AppendEntryLine(sessionId, entry);
            if (titleChanged)
            {
                WriteMeta(header);
            }

            var detail = new SessionDetail { Header = header, Entries = entries };
            RememberLive(sessionId, detail, appendedEntry: entry);
            // Snapshot so ASP.NET can serialize after the gate is released without racing the next Append.
            return Snapshot(detail);
        }
    }

    public void MarkToolCatalog(string sessionId, string agentId)
    {
        lock (Gate(sessionId))
        {
            var existing = ReadExisting(sessionId);
            var header = WithHeader(existing.Header, toolCatalogAgentId: agentId);
            WriteMeta(header);
            RememberLive(sessionId, new SessionDetail { Header = header, Entries = existing.Entries });
        }
    }

    public SessionDetail Rename(string sessionId, string title)
    {
        lock (Gate(sessionId))
        {
            var existing = ReadExisting(sessionId);
            var now = DateTimeOffset.UtcNow;
            var header = WithHeader(
                existing.Header,
                title: string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim(),
                updatedAt: now);
            WriteMeta(header);
            var detail = new SessionDetail { Header = header, Entries = existing.Entries };
            RememberLive(sessionId, detail);
            return Snapshot(detail);
        }
    }

    /// <summary>Permanently remove UI JSONL + meta (+ caller should delete .agent.json / attachments).</summary>
    public bool Delete(string sessionId)
    {
        if (!SessionIdRules.IsValid(sessionId))
        {
            return false;
        }

        lock (Gate(sessionId))
        {
            ForgetLive(sessionId);
            var file = PathFor(sessionId);
            var meta = MetaPathFor(sessionId);
            var had = File.Exists(file) || File.Exists(meta);
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            if (File.Exists(meta))
            {
                File.Delete(meta);
            }

            if (had)
            {
                _gates.TryRemove(sessionId, out _);
            }

            return had;
        }
    }

    /// <summary>
    /// Mark archived + persist Summary on the header and a <c>session_archived</c> meta entry.
    /// </summary>
    public SessionDetail Archive(string sessionId, string summary)
    {
        lock (Gate(sessionId))
        {
            var existing = ReadExisting(sessionId);
            if (existing.Header.ArchivedAt is not null)
            {
                return Snapshot(existing);
            }

            var now = DateTimeOffset.UtcNow;
            var trimmed = string.IsNullOrWhiteSpace(summary)
                ? "(empty session)"
                : summary.Trim();
            var metaReq = SessionEvents.Meta(SessionMetaKinds.SessionArchived, new
            {
                summary = trimmed,
                at = now
            });
            var meta = new SessionEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                ParentId = existing.Header.CurrentEntryId,
                Type = metaReq.Type,
                Timestamp = now,
                Payload = metaReq.Payload
            };
            var entries = existing.Entries as List<SessionEntry> ?? existing.Entries.ToList();
            entries.Add(meta);
            EnsureMigratedJsonl(existing);
            AppendEntryLine(sessionId, meta);
            var header = WithHeader(
                existing.Header,
                updatedAt: now,
                currentEntryId: meta.Id,
                archivedAt: now,
                summary: trimmed,
                entryCount: entries.Count);
            WriteMeta(header);
            var archived = new SessionDetail { Header = header, Entries = entries };
            RememberLive(sessionId, archived);
            return Snapshot(archived);
        }
    }

    private static SessionHeader WithHeader(
        SessionHeader source,
        string? title = null,
        DateTimeOffset? updatedAt = null,
        string? currentEntryId = null,
        DateTimeOffset? archivedAt = null,
        string? summary = null,
        bool clearArchive = false,
        int? entryCount = null,
        string? toolCatalogAgentId = null) =>
        new()
        {
            SessionId = source.SessionId,
            AgentId = source.AgentId,
            Title = title ?? source.Title,
            CreatedAt = source.CreatedAt,
            UpdatedAt = updatedAt ?? source.UpdatedAt,
            CurrentEntryId = currentEntryId ?? source.CurrentEntryId,
            ArchivedAt = clearArchive ? null : (archivedAt ?? source.ArchivedAt),
            Summary = clearArchive ? null : (summary ?? source.Summary),
            EntryCount = entryCount ?? source.EntryCount,
            ToolCatalogAgentId = toolCatalogAgentId ?? source.ToolCatalogAgentId,
            WorkspaceId = source.WorkspaceId,
            WorkspacePath = source.WorkspacePath
        };

    /// <summary>First user turn becomes the session title so History stays distinguishable.</summary>
    public static string? TitleFromUserMessage(JsonElement? payload)
    {
        if (payload is not JsonElement el || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? text = null;
        if (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
        {
            text = t.GetString();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var flat = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        if (flat.Length == 0)
        {
            return null;
        }

        return flat.Length <= 40 ? flat : flat[..40] + "…";
    }

    /// <summary>reject → rejected; approve/retry from pending|failed → approving (apply happens outside).</summary>
    public SessionDetail BeginApprovalDecision(string sessionId, string entryId, DecideApprovalRequest request)
    {
        lock (Gate(sessionId))
        {
            var next = ApprovalWorkflow.BeginDecision(ReadExisting(sessionId), entryId, request);
            WriteFile(next.Header, next.Entries);
            return Snapshot(next);
        }
    }

    public SessionDetail CompleteApproval(string sessionId, string entryId, bool success, string? failMessage = null)
    {
        lock (Gate(sessionId))
        {
            var next = ApprovalWorkflow.Complete(ReadExisting(sessionId), entryId, success, failMessage);
            WriteFile(next.Header, next.Entries);
            return Snapshot(next);
        }
    }

    /// <summary>
    /// After the active pending card is decided, promote the next queued approval to pending.
    /// No-op if a pending card already exists or the queue is empty.
    /// </summary>
    public SessionDetail PromoteNextQueuedApproval(string sessionId)
    {
        lock (Gate(sessionId))
        {
            var existing = ReadExisting(sessionId);
            var next = ApprovalWorkflow.PromoteNextQueued(existing);
            if (next.Entries.Count == existing.Entries.Count
                && next.Header.CurrentEntryId == existing.Header.CurrentEntryId)
            {
                return Snapshot(existing);
            }

            WriteFile(next.Header, next.Entries);
            return Snapshot(next);
        }
    }

    private SessionDetail ReadExisting(string sessionId)
    {
        if (_live.TryGetValue(sessionId, out var cached))
        {
            return cached;
        }

        var file = PathFor(sessionId);
        var metaPath = MetaPathFor(sessionId);
        if (!File.Exists(file) && !File.Exists(metaPath))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");
        }

        SessionDetail? detail = null;
        if (File.Exists(file))
        {
            detail = ReadFile(file);
        }

        var meta = File.Exists(metaPath) ? ReadMetaFile(metaPath) : null;
        if (detail is null && meta is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");
        }

        SessionDetail loaded;
        if (detail is null)
        {
            loaded = new SessionDetail
            {
                Header = WithHeader(meta!, entryCount: 0),
                Entries = new List<SessionEntry>()
            };
        }
        else if (meta is not null)
        {
            // Prefer sidecar header fields; EntryCount / currentEntryId / updatedAt follow JSONL
            // so skipped meta writes on hot Append still recover cleanly after restart.
            var last = detail.Entries.Count > 0 ? detail.Entries[^1] : null;
            var header = WithHeader(
                meta,
                updatedAt: last?.Timestamp ?? meta.UpdatedAt,
                currentEntryId: last?.Id ?? meta.CurrentEntryId,
                entryCount: detail.Entries.Count);
            loaded = new SessionDetail
            {
                Header = header,
                Entries = detail.Entries as List<SessionEntry> ?? detail.Entries.ToList()
            };
        }
        else
        {
            loaded = new SessionDetail
            {
                Header = WithHeader(detail.Header, entryCount: detail.Entries.Count),
                Entries = detail.Entries as List<SessionEntry> ?? detail.Entries.ToList()
            };
        }

        RememberLive(sessionId, loaded);
        return loaded;
    }

    /// <summary>
    /// Pin a session in the hot cache (e.g. for the life of an SSE subscription).
    /// Eviction skips pinned ids so long-lived watches do not re-parse JSONL every tick.
    /// </summary>
    public IDisposable PinLive(string sessionId)
    {
        _livePins.AddOrUpdate(sessionId, 1, static (_, n) => n + 1);
        _ = Get(sessionId);
        return new LivePin(this, sessionId);
    }

    private int ResolveCursorIndex(
        string sessionId,
        IReadOnlyList<SessionEntry> entries,
        string afterEntryId)
    {
        if (_liveIdIndex.TryGetValue(sessionId, out var map)
            && map.TryGetValue(afterEntryId, out var mapped)
            && mapped >= 0
            && mapped < entries.Count
            && string.Equals(entries[mapped].Id, afterEntryId, StringComparison.OrdinalIgnoreCase))
        {
            _syncCursor[sessionId] = (afterEntryId, mapped);
            return mapped;
        }

        if (_syncCursor.TryGetValue(sessionId, out var cached)
            && string.Equals(cached.EntryId, afterEntryId, StringComparison.OrdinalIgnoreCase)
            && cached.Index >= 0
            && cached.Index < entries.Count
            && string.Equals(entries[cached.Index].Id, afterEntryId, StringComparison.OrdinalIgnoreCase))
        {
            return cached.Index;
        }

        var index = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].Id, afterEntryId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            _syncCursor[sessionId] = (afterEntryId, index);
        }
        else
        {
            _syncCursor.TryRemove(sessionId, out _);
        }

        return index;
    }

    /// <summary>
    /// Detach <see cref="SessionEntry"/> list from the hot cache so callers can serialize after the gate unlocks.
    /// </summary>
    private static SessionDetail Snapshot(SessionDetail detail) =>
        new()
        {
            Header = detail.Header,
            Entries = detail.Entries.ToList()
        };

    private void RememberLive(string sessionId, SessionDetail detail, SessionEntry? appendedEntry = null)
    {
        _live[sessionId] = detail;
        UpdateLiveIdIndex(sessionId, detail.Entries, appendedEntry);

        // Keep cursor cache when the remembered index still points at the same entry id.
        if (_syncCursor.TryGetValue(sessionId, out var cached)
            && (cached.Index < 0
                || cached.Index >= detail.Entries.Count
                || !string.Equals(
                    detail.Entries[cached.Index].Id,
                    cached.EntryId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            _syncCursor.TryRemove(sessionId, out _);
        }

        if (_live.Count <= MaxLiveSessions)
        {
            return;
        }

        var overflow = _live.Count - MaxLiveSessions;
        var victims = _live
            .Where(kv =>
                !string.Equals(kv.Key, sessionId, StringComparison.OrdinalIgnoreCase)
                && !_livePins.ContainsKey(kv.Key))
            .OrderBy(kv => kv.Value.Header.UpdatedAt)
            .Take(overflow)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in victims)
        {
            ForgetLive(id);
        }
    }

    /// <summary>
    /// Append path: O(1) index update. Full rebuild only when the cache is missing or out of sync.
    /// </summary>
    private void UpdateLiveIdIndex(
        string sessionId,
        IReadOnlyList<SessionEntry> entries,
        SessionEntry? appendedEntry)
    {
        if (appendedEntry is not null
            && _liveIdIndex.TryGetValue(sessionId, out var map)
            && map.Count == entries.Count - 1)
        {
            map[appendedEntry.Id] = entries.Count - 1;
            return;
        }

        // Header-only refresh: reuse map when entry count still matches.
        if (appendedEntry is null
            && _liveIdIndex.TryGetValue(sessionId, out var existing)
            && existing.Count == entries.Count)
        {
            return;
        }

        _liveIdIndex[sessionId] = BuildIdIndex(entries);
    }

    private static Dictionary<string, int> BuildIdIndex(IReadOnlyList<SessionEntry> entries)
    {
        var map = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            map[entries[i].Id] = i;
        }

        return map;
    }

    private void ForgetLive(string sessionId)
    {
        _live.TryRemove(sessionId, out _);
        _liveIdIndex.TryRemove(sessionId, out _);
        _syncCursor.TryRemove(sessionId, out _);
    }

    private void ReleaseLivePin(string sessionId)
    {
        while (true)
        {
            if (!_livePins.TryGetValue(sessionId, out var n))
            {
                return;
            }

            if (n <= 1)
            {
                if (_livePins.TryRemove(new KeyValuePair<string, int>(sessionId, n)))
                {
                    return;
                }

                continue;
            }

            if (_livePins.TryUpdate(sessionId, n - 1, n))
            {
                return;
            }
        }
    }

    private sealed class LivePin(JsonlSessionStore store, string sessionId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            store.ReleaseLivePin(sessionId);
        }
    }

    private object Gate(string sessionId) => _gates.GetOrAdd(sessionId, static _ => new object());

    private string PathFor(string sessionId)
    {
        var id = SessionIdRules.RequireValid(sessionId);
        return Path.Combine(paths.SessionsDir, $"{id}.jsonl");
    }

    private string MetaPathFor(string sessionId)
    {
        var id = SessionIdRules.RequireValid(sessionId);
        return Path.Combine(paths.SessionsDir, $"{id}.meta.json");
    }

    private SessionHeader? ReadMetaFile(string metaFile)
    {
        var json = File.ReadAllText(metaFile);
        return JsonSerializer.Deserialize<SessionHeader>(json, JsonOptions);
    }

    private void WriteMeta(SessionHeader header)
    {
        Directory.CreateDirectory(paths.SessionsDir);
        var path = MetaPathFor(header.SessionId);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(header, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    private void AppendEntryLine(string sessionId, SessionEntry entry)
    {
        Directory.CreateDirectory(paths.SessionsDir);
        var line = JsonSerializer.Serialize(new { record = "entry", data = entry }, JsonOptions) + "\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        var file = PathFor(sessionId);
        // Truncate a crash-torn partial last line before appending (avoids glued corrupt records).
        RepairTrailingPartialLine(file);
        // Share read with Shell polls; exclusive writers serialize via Gate(sessionId).
        using var stream = new FileStream(
            file,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes, 0, bytes.Length);
        // Durable fsync for user-visible / recovery-critical types; OS flush for high-churn tool/thinking/usage.
        var fsync = entry.Type is SessionEventTypes.User
            or SessionEventTypes.Assistant
            or SessionEventTypes.Approval
            or SessionEventTypes.Error;
        stream.Flush(flushToDisk: fsync);
    }

    /// <summary>
    /// If the file does not end with '\n', truncate to the last complete line
    /// (hard-kill mid-write left a partial JSONL record).
    /// </summary>
    internal static void RepairTrailingPartialLine(string file)
    {
        if (!File.Exists(file))
        {
            return;
        }

        using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            return;
        }

        stream.Seek(-1, SeekOrigin.End);
        var last = stream.ReadByte();
        if (last == '\n')
        {
            return;
        }

        long cut = stream.Length;
        const int chunk = 4096;
        var buffer = new byte[chunk];
        while (cut > 0)
        {
            var readFrom = Math.Max(0, cut - chunk);
            var toRead = (int)(cut - readFrom);
            stream.Seek(readFrom, SeekOrigin.Begin);
            var read = stream.Read(buffer, 0, toRead);
            for (var i = read - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n')
                {
                    stream.SetLength(readFrom + i + 1);
                    stream.Flush(flushToDisk: true);
                    return;
                }
            }

            cut = readFrom;
        }

        // No newline anywhere — file is a single torn line; clear it.
        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Once: drop embedded header lines from JSONL so subsequent AppendEntryLine stays entry-only.
    /// </summary>
    private void EnsureMigratedJsonl(SessionDetail existing)
    {
        var metaPath = MetaPathFor(existing.Header.SessionId);
        if (File.Exists(metaPath))
        {
            // Already on sidecar header; if JSONL still has a header record, rewrite entries-only once.
            var file = PathFor(existing.Header.SessionId);
            if (!File.Exists(file))
            {
                return;
            }

            // Cheap check: first non-empty line.
            string? first = null;
            using (var reader = new StreamReader(file))
            {
                while (reader.ReadLine() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        first = line;
                        break;
                    }
                }
            }

            if (first is null || !first.Contains("\"record\":\"header\"", StringComparison.Ordinal))
            {
                return;
            }

            WriteEntriesOnly(existing.Header.SessionId, existing.Entries);
            return;
        }

        var header = WithHeader(
            existing.Header,
            entryCount: existing.Header.EntryCount > 0
                ? existing.Header.EntryCount
                : existing.Entries.Count);
        WriteMeta(header);
        WriteEntriesOnly(existing.Header.SessionId, existing.Entries);
    }

    private void WriteEntriesOnly(string sessionId, IReadOnlyList<SessionEntry> entries)
    {
        Directory.CreateDirectory(paths.SessionsDir);
        var file = PathFor(sessionId);
        var temp = Path.Combine(paths.SessionsDir, $".{sessionId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                foreach (var entry in entries)
                {
                    writer.WriteLine(JsonSerializer.Serialize(new { record = "entry", data = entry }, JsonOptions));
                }

                writer.Flush();
            }

            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    private SessionDetail? ReadFile(string file, bool repair = true)
    {
        IOException? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (repair)
                {
                    RepairTrailingPartialLine(file);
                }

                using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                {
                    lines.Add(line);
                }

                return ParseLines(lines);
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(15 * (attempt + 1));
            }
        }

        throw last ?? new IOException($"Unable to read session file '{file}'.");
    }

    private static SessionDetail? ParseLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        SessionHeader? header = null;
        var entries = new List<SessionEntry>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var kind = root.GetProperty("record").GetString();
                if (kind == "header")
                {
                    header = JsonSerializer.Deserialize<SessionHeader>(root.GetProperty("data").GetRawText(), JsonOptions);
                }
                else if (kind == "entry")
                {
                    var entry = JsonSerializer.Deserialize<SessionEntry>(root.GetProperty("data").GetRawText(), JsonOptions);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
            }
            catch (JsonException)
            {
                // Skip corrupt lines so one bad record does not break the session / list.
            }
        }

        // Append-only format: JSONL is entry-only; header lives in *.meta.json.
        // Do not drop entries when the embedded header line was migrated away.
        if (header is null && entries.Count == 0)
        {
            return null;
        }

        return new SessionDetail
        {
            // Placeholder header when JSONL has only entries; ReadExisting overlays *.meta.json.
            Header = header ?? new SessionHeader { SessionId = "", AgentId = "" },
            Entries = entries
        };
    }



    /// <summary>Rare full rewrite (in-place approval promote). Prefer <see cref="Append"/>.</summary>
    private void WriteFile(SessionHeader header, IReadOnlyList<SessionEntry> entries)
    {
        var normalized = WithHeader(
            header,
            entryCount: entries.Count);
        // Entries first, then meta — never leave EntryCount ahead of JSONL.
        WriteEntriesOnly(normalized.SessionId, entries);
        WriteMeta(normalized);
        RememberLive(
            normalized.SessionId,
            new SessionDetail
            {
                Header = normalized,
                Entries = entries as List<SessionEntry> ?? entries.ToList()
            });
    }
}
