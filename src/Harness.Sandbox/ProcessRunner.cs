using System.Diagnostics;
using System.Text;
using Harness.Sdk;

namespace Harness.Sandbox;

/// <summary>Runs a process to completion, keeping the head and tail of its combined output and spilling the rest to a file.</summary>
public static class ProcessRunner
{
    public static async Task<ExecResult> RunAsync(ProcessStartInfo psi, ExecRequest request, string? spillDirectory, CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        Stopwatch clock = Stopwatch.StartNew();
        using Process process = new() { StartInfo = psi };
        HeadTailBuffer buffer = new(request.OutputHeadTailBytes, spillDirectory);
        process.Start();

        Task pumpOut = PumpAsync(process.StandardOutput, buffer);
        Task pumpErr = PumpAsync(process.StandardError, buffer);
        try
        {
            if (request.StandardInput is { } input) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        catch (IOException) { }   // the process may exit without reading stdin

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (ct.IsCancellationRequested) { buffer.Dispose(); throw; }
            timedOut = true;
        }
        // Background children may keep the pipes open; do not wait for them forever.
        await Task.WhenAny(Task.WhenAll(pumpOut, pumpErr), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

        (string output, bool truncated, long total, string? spill) = buffer.Finish();
        return new ExecResult(timedOut ? -1 : process.ExitCode, output, timedOut, truncated, total, spill, clock.Elapsed);
    }

    public static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task PumpAsync(StreamReader reader, HeadTailBuffer buffer)
    {
        char[] chunk = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(chunk)) > 0) buffer.Append(chunk.AsSpan(0, read));
    }
}

/// <summary>Keeps the first and last <c>limit</c> characters of a stream; when it overflows, the whole stream goes to a spill file.</summary>
internal sealed class HeadTailBuffer(int limit, string? spillDirectory) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _head = new();
    private readonly char[] _tail = new char[Math.Max(1, limit)];
    private readonly StringBuilder _pending = new();
    private int _tailStart, _tailCount;
    private long _total;
    private StreamWriter? _spill;
    private string? _spillPath;

    public void Append(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            _total += text.Length;
            // Everything is kept verbatim while it fits in head + tail; past that it streams to the spill file (if any) instead of memory.
            if (_spill is not null) _spill.Write(text);
            else if (_pending.Length + text.Length <= limit * 2) _pending.Append(text);
            else if (spillDirectory is not null)
            {
                Directory.CreateDirectory(spillDirectory);
                _spillPath = Path.Combine(spillDirectory, $"exec_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Random.Shared.Next(100000, 999999)}.txt");
                _spill = new StreamWriter(_spillPath, append: false, Encoding.UTF8);
                _spill.Write(_pending.ToString());
                _spill.Write(text);
                _pending.Clear();
            }
            else _pending.Clear();
            int headRoom = limit - _head.Length;
            if (headRoom > 0)
            {
                int take = Math.Min(headRoom, text.Length);
                _head.Append(text[..take]);
                text = text[take..];
            }
            foreach (char c in text)
            {
                int index = (_tailStart + _tailCount) % _tail.Length;
                _tail[index] = c;
                if (_tailCount < _tail.Length) _tailCount++;
                else _tailStart = (_tailStart + 1) % _tail.Length;
            }
        }
    }

    public (string Output, bool Truncated, long Total, string? SpillPath) Finish()
    {
        lock (_gate)
        {
            _spill?.Dispose();
            _spill = null;
            long skipped = _total - _head.Length - _tailCount;
            if (skipped <= 0) return (_pending.ToString(), false, _total, null);
            StringBuilder tail = new(_tailCount);
            for (int i = 0; i < _tailCount; i++) tail.Append(_tail[(_tailStart + i) % _tail.Length]);
            string output = $"{_head}\n… [{skipped:N0} chars omitted] …\n{tail}";
            return (output, true, _total, _spillPath);
        }
    }

    public void Dispose()
    {
        _spill?.Dispose();
        if (_spillPath is not null) try { File.Delete(_spillPath); } catch (IOException) { }
    }
}

/// <summary>A started process exposed as an <see cref="ISandboxProcess"/>.</summary>
internal sealed class LocalSandboxProcess(Process process, Func<ValueTask>? onDispose = null) : ISandboxProcess
{
    public int? ProcessId => process.HasExited ? null : process.Id;
    public Stream StandardInput => process.StandardInput.BaseStream;
    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public Stream StandardError => process.StandardError.BaseStream;
    public bool HasExited => process.HasExited;
    public int? ExitCode => process.HasExited ? process.ExitCode : null;
    public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);
    public void Kill() => ProcessRunner.KillTree(process);

    public async ValueTask DisposeAsync()
    {
        Kill();
        process.Dispose();
        if (onDispose is not null) await onDispose();
    }

    public static LocalSandboxProcess Start(ProcessStartInfo psi, Func<ValueTask>? onDispose = null)
    {
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        Process process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}.");
        return new LocalSandboxProcess(process, onDispose);
    }
}
