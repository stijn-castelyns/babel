using System.Collections.Concurrent;
using System.Text.Json;
using Harness.Sdk;

namespace Harness.Core.Sessions;

/// <summary>
/// Sessions as folders of append-only JSONL files under <c>sessions/yyyy/MM/&lt;id&gt;/</c>.
/// The files are the source of truth; the SQLite index can always be rebuilt from them.
/// </summary>
public sealed class SessionStore(HarnessPaths paths, HarnessDb db)
{
    private readonly ConcurrentDictionary<string, SessionFolder> _open = new();

    public HarnessDb Index => db;

    public SessionFolder Create(SessionInfo info)
    {
        if (string.IsNullOrEmpty(info.Id)) info.Id = Ids.NewSessionId();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (info.CreatedAt == default) info.CreatedAt = now;
        info.UpdatedAt = now;
        string dir = Path.Combine(paths.SessionsDir, info.CreatedAt.ToString("yyyy"), info.CreatedAt.ToString("MM"), info.Id);
        Directory.CreateDirectory(Path.Combine(dir, "artifacts"));
        SessionFolder folder = new(dir, info, db);
        folder.Save();
        _open[info.Id] = folder;
        return folder;
    }

    public SessionFolder Open(string id) =>
        TryOpen(id) ?? throw new KeyNotFoundException($"Session '{id}' not found.");

    public SessionFolder? TryOpen(string id)
    {
        if (_open.TryGetValue(id, out SessionFolder? cached)) return cached;
        string? dir = Locate(id);
        if (dir is null) return null;
        SessionFolder folder = SessionFolder.Load(dir, db);
        return _open.GetOrAdd(id, folder);
    }

    public SessionFolder? FindByKey(string key) =>
        db.FindSessionIdByKey(key) is { } id ? TryOpen(id) : null;

    public IEnumerable<SessionFolder> All()
    {
        if (!Directory.Exists(paths.SessionsDir)) yield break;
        foreach (string file in Directory.EnumerateFiles(paths.SessionsDir, "session.json", SearchOption.AllDirectories))
        {
            SessionFolder? folder = null;
            try { folder = TryOpen(Path.GetFileName(Path.GetDirectoryName(file)!)); }
            catch (JsonException) { }   // a corrupt session.json must not break listing every other session
            if (folder is not null) yield return folder;
        }
    }

    /// <summary>Creates a new session whose history is a copy of <paramref name="sourceId"/> up to and including <paramref name="atSeq"/>.</summary>
    public SessionFolder Fork(string sourceId, long? atSeq, string? title = null)
    {
        SessionFolder source = Open(sourceId);
        long seq = atSeq ?? source.Info.MessageCount;
        SessionFolder fork = Create(new SessionInfo
        {
            Agent = source.Info.Agent,
            Model = source.Info.Model,
            Workspace = source.Info.Workspace,
            WorkspaceName = source.Info.WorkspaceName,
            WorkingDirectory = source.Info.WorkingDirectory,
            Title = title ?? $"{source.Info.Title ?? source.Info.Id} (fork at #{seq})",
            ParentId = source.Info.Id,
            ForkSeq = seq,
            Tags = [.. source.Info.Tags],
        });
        List<HistoryEntry> entries = [.. source.ReadHistory().Where(e => e.Seq <= seq)];
        fork.AppendHistory(entries.Select(e => e.Message), runId: null);
        return fork;
    }

    public bool Delete(string id)
    {
        SessionFolder? folder = TryOpen(id);
        if (folder is null) return false;
        using (folder.AcquireLease("delete"))
        {
            _open.TryRemove(id, out _);
        }
        Directory.Delete(folder.Directory, recursive: true);
        db.DeleteSession(id);
        return true;
    }

    /// <summary>Rebuilds the SQLite session index from the folders.</summary>
    public int Reindex()
    {
        db.ClearSessions();
        int count = 0;
        foreach (SessionFolder folder in All())
        {
            db.UpsertSession(folder.Info);
            db.IndexMessages(folder.Info.Id, folder.ReadHistory());
            count++;
        }
        return count;
    }

    private string? Locate(string id)
    {
        if (db.GetSessionPath(id) is { } indexed && File.Exists(Path.Combine(indexed, "session.json"))) return indexed;
        if (!Directory.Exists(paths.SessionsDir) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return null;
        foreach (string year in Directory.EnumerateDirectories(paths.SessionsDir))
            foreach (string month in Directory.EnumerateDirectories(year))
            {
                string dir = Path.Combine(month, id);
                if (File.Exists(Path.Combine(dir, "session.json"))) return dir;
            }
        return null;
    }
}

/// <summary>One session's folder. Writers must hold the lease from <see cref="AcquireLease"/>.</summary>
public sealed class SessionFolder
{
    private readonly HarnessDb _db;
    private readonly Lock _gate = new();
    private long _nextHistorySeq;
    private long _nextEventSeq;

    internal SessionFolder(string directory, SessionInfo info, HarnessDb db)
    {
        Directory = directory;
        Info = info;
        _db = db;
        _nextHistorySeq = info.MessageCount + 1;
        _nextEventSeq = CountLines(EventsFile) + 1;
    }

    internal static SessionFolder Load(string directory, HarnessDb db)
    {
        SessionInfo info = JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(Path.Combine(directory, "session.json")), SessionJson.Options)
            ?? throw new InvalidDataException($"{directory}/session.json is empty.");
        SessionFolder folder = new(directory, info, db);
        // session.json may lag behind history.jsonl after a crash; the history file wins.
        long last = folder.ReadHistory().Select(e => e.Seq).DefaultIfEmpty(0).Max();
        if (last != info.MessageCount)
        {
            info.MessageCount = last;
            folder._nextHistorySeq = last + 1;
        }
        return folder;
    }

    public string Directory { get; }
    public SessionInfo Info { get; }
    public string Id => Info.Id;
    public string HistoryFile => Path.Combine(Directory, "history.jsonl");
    public string EventsFile => Path.Combine(Directory, "events.jsonl");
    public string CheckpointsFile => Path.Combine(Directory, "checkpoints.jsonl");
    public string LockFile => Path.Combine(Directory, "lock");
    public string ArtifactsDir => Path.Combine(Directory, "artifacts");

    public void Save()
    {
        lock (_gate)
        {
            Info.UpdatedAt = DateTimeOffset.UtcNow;
            string file = Path.Combine(Directory, "session.json");
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Info, SessionJson.Indented));
            File.Move(tmp, file, overwrite: true);
        }
        _db.UpsertSession(Info, Directory);
    }

    /// <summary>
    /// Takes the single-writer lease. Throws <see cref="SessionBusyException"/> when another run (in this or another process) holds it.
    /// </summary>
    public IDisposable AcquireLease(string runId)
    {
        try
        {
            FileStream stream = new(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using (StreamWriter w = new(stream, leaveOpen: true)) w.Write($"{Environment.ProcessId} {runId} {DateTimeOffset.UtcNow:O}");
            stream.Flush();
            return stream;
        }
        catch (IOException ex)
        {
            throw new SessionBusyException(Id, ex);
        }
    }

    public IEnumerable<HistoryEntry> ReadHistory()
    {
        foreach (JsonElement line in ReadJsonLines(HistoryFile))
        {
            HistoryEntry? entry = null;
            try { entry = line.Deserialize<HistoryEntry>(SessionJson.Options); }
            catch (JsonException) { }
            if (entry is not null) yield return entry;
        }
    }

    public IReadOnlyList<HistoryEntry> AppendHistory(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, string? runId)
    {
        List<HistoryEntry> written = [];
        lock (_gate)
        {
            using StreamWriter w = new(HistoryFile, append: true);
            foreach (Microsoft.Extensions.AI.ChatMessage message in messages)
            {
                HistoryEntry entry = new(_nextHistorySeq++, DateTimeOffset.UtcNow, runId, message);
                w.WriteLine(JsonSerializer.Serialize(entry, SessionJson.Options));
                written.Add(entry);
            }
            w.Flush();
            Info.MessageCount = _nextHistorySeq - 1;
        }
        if (written.Count > 0)
        {
            Save();
            _db.IndexMessages(Id, written);
        }
        return written;
    }

    public IEnumerable<CompactionCheckpoint> ReadCheckpoints()
    {
        foreach (JsonElement line in ReadJsonLines(CheckpointsFile))
            if (line.Deserialize<CompactionCheckpoint>(SessionJson.Options) is { } cp) yield return cp;
    }

    public void AppendCheckpoint(CompactionCheckpoint checkpoint)
    {
        lock (_gate) File.AppendAllText(CheckpointsFile, JsonSerializer.Serialize(checkpoint, SessionJson.Options) + "\n");
    }

    /// <summary>Assigns the next session event sequence number and, for persisted types, appends the event to <c>events.jsonl</c>.</summary>
    public HarnessEvent AppendEvent(string? runId, string type, System.Text.Json.Nodes.JsonObject data)
    {
        lock (_gate)
        {
            // Live-only events carry the last persisted sequence number, so a client resuming from them never skips a persisted event.
            bool persisted = EventTypes.IsPersisted(type);
            HarnessEvent evt = new(persisted ? _nextEventSeq : _nextEventSeq - 1, DateTimeOffset.UtcNow, runId, Id, type, data);
            if (persisted)
            {
                _nextEventSeq++;
                File.AppendAllText(EventsFile, JsonSerializer.Serialize(evt, SessionJson.Options) + "\n");
            }
            return evt;
        }
    }

    public IEnumerable<HarnessEvent> ReadEvents(long afterSeq = 0, string? runId = null)
    {
        foreach (JsonElement line in ReadJsonLines(EventsFile))
        {
            HarnessEvent? evt = null;
            try { evt = line.Deserialize<HarnessEvent>(SessionJson.Options); }
            catch (JsonException) { }
            if (evt is not null && evt.Seq > afterSeq && (runId is null || evt.RunId == runId)) yield return evt;
        }
    }

    /// <summary>Reads JSON lines, skipping a torn last line (or any unparsable one) instead of failing.</summary>
    private static IEnumerable<JsonElement> ReadJsonLines(string file)
    {
        if (!File.Exists(file)) yield break;
        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            JsonElement element;
            try { element = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { continue; }
            yield return element;
        }
    }

    private static long CountLines(string file)
    {
        if (!File.Exists(file)) return 0;
        long count = 0;
        foreach (string line in File.ReadLines(file)) if (line.Length > 0) count++;
        return count;
    }
}

public sealed class SessionBusyException(string sessionId, Exception? inner = null)
    : Exception($"Session '{sessionId}' is busy: another run holds its lease.", inner)
{
    public string SessionId { get; } = sessionId;
}
