using Harness.Runs;
using Harness.Sdk;
using Microsoft.Extensions.Hosting;

namespace Harness.Server.Push;

/// <summary>
/// Tells subscribed phones when a person is needed: an approval that is still waiting a moment after it was requested
/// (policy, hooks and session grants answer at once, so those never notify), and a triggered run that finished. A
/// notification only opens the app: an approval still needs the passkey step-up there, which a service worker cannot do.
/// </summary>
public sealed class PushNotifier(RunOrchestrator runs, PushSender sender) : BackgroundService
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using EventHub.Subscription sub = runs.Events.Subscribe(e => e.Type is EventTypes.ApprovalRequested or EventTypes.RunFinished);
        try
        {
            await foreach (EventHub.Envelope envelope in sub.Reader.ReadAllAsync(stoppingToken))
                _ = Task.Run(() => HandleAsync(envelope.Event, stoppingToken), stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(HarnessEvent e, CancellationToken ct)
    {
        try
        {
            if (e.Type == EventTypes.ApprovalRequested)
            {
                await Task.Delay(Settle, ct);
                string? requestId = e.Data["requestId"]?.GetValue<string>();
                if (runs.Approvals.Pending().FirstOrDefault(p => p.RequestId == requestId) is not { } pending) return;
                await sender.SendAsync(new
                {
                    title = $"Approval needed: {pending.ToolName}",
                    body = Shorten(pending.Summary ?? pending.Arguments.ToJsonString()),
                    url = $"/#/run/{pending.RunId}",
                    tag = pending.RequestId,
                }, urgent: true, ct);
            }
            else if (e.RunId is { } runId && runs.Get(runId) is { TriggerId: { } trigger } run)
            {
                string state = e.Data["state"]?.GetValue<string>() ?? run.State;
                await sender.SendAsync(new
                {
                    title = $"{trigger}: {state.Replace('_', ' ')}",
                    body = Shorten(e.Data["error"]?.GetValue<string>() ?? e.Data["text"]?.GetValue<string>() ?? ""),
                    url = $"/#/run/{runId}",
                    tag = runId,
                }, urgent: state != "succeeded", ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string Shorten(string s) => s.Length <= 180 ? s : s[..179] + "…";
}
