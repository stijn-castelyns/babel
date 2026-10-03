using System.ComponentModel;
using System.Globalization;
using System.Text;
using Harness.Core.Agents;
using Harness.Sdk;

namespace Harness.Tools;

/// <summary>
/// Runs commands through the run's sandbox: <c>bash -lc</c> on Linux and macOS, <c>pwsh</c> on Windows.
/// Background commands return a handle; calling shell again with that handle reads new output or stops the job.
/// </summary>
public sealed class ShellTool(IWorkspace workspace, ISandbox sandbox, SessionRuntimeState state)
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MaxTimeoutSeconds = 600;
    public const int HeadTailBytes = 8 * 1024;   // 16 KB of output in total: head plus tail
    private const int MaxBackgroundBuffer = 256 * 1024;

    [Description("Run a shell command in the workspace. Output shows head and tail; the full output is saved to a file when it is large. Set background for long-running processes such as servers, then call again with handle to read output or stop it.")]
    public async Task<string> Shell(
        [Description("The command to run")] string? command = null,
        [Description("Working directory relative to the workspace root")] string? workdir = null,
        [Description("Timeout in seconds (default 120, max 600)")] int timeoutSeconds = DefaultTimeoutSeconds,
        [Description("Start the command in the background and return a handle")] bool background = false,
        [Description("Handle of a background command: returns its new output")] string? handle = null,
        [Description("With handle: stop the background command")] bool stop = false,
        CancellationToken cancellationToken = default)
    {
        if (handle is not null) return Poll(handle, stop);
        if (string.IsNullOrWhiteSpace(command)) return "Error: command is required (or pass handle to read a background job).";

        string dir = workspace.Resolve(workdir ?? ".");
        if (!Directory.Exists(dir)) return $"Error: working directory '{workspace.Display(dir)}' does not exist.";
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, MaxTimeoutSeconds);
        ExecRequest request = new()
        {
            Command = command,
            WorkingDirectory = dir,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            OutputHeadTailBytes = HeadTailBytes,
        };

        if (background) return await StartBackgroundAsync(request, cancellationToken);

        ExecResult result = await sandbox.ExecAsync(request, cancellationToken);
        StringBuilder sb = new();
        sb.Append(result.TimedOut ? $"timed out after {timeoutSeconds}s" : $"exit {result.ExitCode}");
        sb.Append($" · {result.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s · cwd {workspace.Display(dir)}");
        if (result.Truncated)
        {
            sb.Append($" · {result.TotalOutputBytes.ToString("N0", CultureInfo.InvariantCulture)} chars of output, head and tail shown");
            if (result.FullOutputPath is { } spill) sb.Append($" · full output: {workspace.Display(spill)}");
        }
        sb.Append('\n');
        sb.Append(result.Output.Length == 0 ? "(no output)" : result.Output.TrimEnd());
        return sb.ToString();
    }

    private async Task<string> StartBackgroundAsync(ExecRequest request, CancellationToken ct)
    {
        ISandboxProcess process = await sandbox.StartAsync(request, ct);
        string handle = "bg_" + (state.BackgroundJobs.Count + 1).ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..4];
        BackgroundJob job = new(request.Command, process);
        state.BackgroundJobs[handle] = job;
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);   // let fast failures show up immediately
        return $"started in background · handle={handle} · pid {process.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "?"}\n" + job.TakeNewOutput();
    }

    private string Poll(string handle, bool stop)
    {
        if (!state.BackgroundJobs.TryGetValue(handle, out object? value) || value is not BackgroundJob job)
            return $"Error: no background job with handle '{handle}'.";
        if (stop)
        {
            job.Process.Kill();
            state.BackgroundJobs.TryRemove(handle, out _);
        }
        string status = job.Process.HasExited ? $"exited {job.Process.ExitCode}" : stop ? "stopped" : "running";
        string output = job.TakeNewOutput();
        return $"{handle} · {status} · {job.Command}\n{(output.Length == 0 ? "(no new output)" : output.TrimEnd())}";
    }

    /// <summary>A background process whose stdout and stderr are pumped into a bounded buffer.</summary>
    private sealed class BackgroundJob
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _buffer = new();
        private long _dropped;

        public BackgroundJob(string command, ISandboxProcess process)
        {
            Command = command;
            Process = process;
            _ = PumpAsync(process.StandardOutput);
            _ = PumpAsync(process.StandardError);
        }

        public string Command { get; }
        public ISandboxProcess Process { get; }

        public string TakeNewOutput()
        {
            lock (_gate)
            {
                string text = _dropped > 0 ? $"… [{_dropped:N0} chars dropped] …\n{_buffer}" : _buffer.ToString();
                _buffer.Clear();
                _dropped = 0;
                return text.Length > HeadTailBytes * 2 ? text[..HeadTailBytes] + "\n…\n" + text[^HeadTailBytes..] : text;
            }
        }

        private async Task PumpAsync(Stream stream)
        {
            using StreamReader reader = new(stream, Encoding.UTF8);
            char[] chunk = new char[4096];
            int read;
            try
            {
                while ((read = await reader.ReadAsync(chunk)) > 0)
                    lock (_gate)
                    {
                        _buffer.Append(chunk, 0, read);
                        if (_buffer.Length > MaxBackgroundBuffer)
                        {
                            int excess = _buffer.Length - MaxBackgroundBuffer;
                            _buffer.Remove(0, excess);
                            _dropped += excess;
                        }
                    }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
