using System.Collections.Concurrent;
using System.Diagnostics;

namespace Harness.Sandbox;

/// <summary>
/// Starts every child process from one dedicated, never-exiting thread.
/// </summary>
/// <remarks>
/// bubblewrap's <c>--die-with-parent</c> uses <c>PR_SET_PDEATHSIG</c>, which fires when the <em>thread</em> that forked the
/// child exits, not the process. .NET forks from the calling thread, and thread-pool threads retire when idle, which would
/// SIGKILL sandboxed commands mid-run. Forking from a thread that lives as long as the daemon keeps the guarantee meaningful.
/// </remarks>
public static class ProcessSpawner
{
    private static readonly BlockingCollection<(ProcessStartInfo Info, TaskCompletionSource<Process> Result)> Queue = [];

    static ProcessSpawner()
    {
        Thread thread = new(Loop) { IsBackground = true, Name = "harness-process-spawner" };
        thread.Start();
    }

    public static Process Start(ProcessStartInfo info)
    {
        TaskCompletionSource<Process> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((info, result));
        return result.Task.GetAwaiter().GetResult();
    }

    private static void Loop()
    {
        foreach ((ProcessStartInfo info, TaskCompletionSource<Process> result) in Queue.GetConsumingEnumerable())
        {
            try
            {
                Process process = new() { StartInfo = info };
                process.Start();
                result.SetResult(process);
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        }
    }
}
