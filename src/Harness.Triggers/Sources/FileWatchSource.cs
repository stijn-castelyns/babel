using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Harness.Core;
using Harness.Sdk;

namespace Harness.Triggers.Sources;

/// <summary>Fires when files appear or change in a folder (inbox folders, drop-a-file workflows), debounced per file.</summary>
public sealed class FileWatchSource : ITriggerSource
{
    public string Type => "file-watch";
    private readonly ConcurrentBag<FileSystemWatcher> _watchers = [];

    public Task StartAsync(TriggerSourceContext context, CancellationToken cancellationToken)
    {
        string path = HarnessPaths.ExpandHome(context.Settings["path"]?.GetValue<string>() ?? throw new InvalidOperationException($"Trigger '{context.TriggerId}' needs source.path."));
        Directory.CreateDirectory(path);
        string filter = context.Settings["pattern"]?.GetValue<string>() ?? "*";
        TimeSpan debounce = context.Settings["debounce"]?.GetValue<string>() is { } d ? Durations.Parse(d) : TimeSpan.FromSeconds(2);
        ConcurrentDictionary<string, CancellationTokenSource> pending = new();

        FileSystemWatcher watcher = new(path, filter) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        void OnChange(object _, FileSystemEventArgs e)
        {
            CancellationTokenSource cts = new();
            if (pending.TryGetValue(e.FullPath, out CancellationTokenSource? old)) old.Cancel();
            pending[e.FullPath] = cts;
            _ = Task.Delay(debounce, cts.Token).ContinueWith(async t =>
            {
                if (t.IsCanceled || !File.Exists(e.FullPath)) return;
                pending.TryRemove(e.FullPath, out CancellationTokenSource? _);
                FileInfo info = new(e.FullPath);
                await context.EmitAsync(new TriggerEvent(
                    $"file:{info.FullName}:{info.LastWriteTimeUtc.Ticks}:{info.Length}", context.TriggerId, DateTimeOffset.UtcNow, null,
                    $"File {info.Name} arrived in {path}.", [new EventAttachment(info.Name, "application/octet-stream", info.FullName, null)],
                    new JsonObject { ["path"] = info.FullName, ["name"] = info.Name, ["size"] = info.Length }, null), CancellationToken.None);
            }, TaskScheduler.Default);
        }
        watcher.Created += OnChange;
        watcher.Changed += OnChange;
        watcher.Renamed += (s, e) => OnChange(s, e);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        while (_watchers.TryTake(out FileSystemWatcher? w)) w.Dispose();
        return Task.CompletedTask;
    }
}
