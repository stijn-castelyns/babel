using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Core.Agents;
using Harness.Core.Config;
using Harness.Sdk;
using Microsoft.Extensions.AI;

namespace Harness.Extensions.Hooks;

/// <summary>
/// Command hooks from <c>config.yaml</c>: the hook context goes to the command as JSON on stdin.
/// Exit 0 continues, exit 2 blocks (stderr is the reason), and JSON on stdout may rewrite arguments or results,
/// or decide an approval. Built on the same <see cref="IHarnessHook"/> API that plugins use.
/// </summary>
public sealed class CommandHooksExtension(ConfigCatalog catalog) : IRunExtension
{
    public IEnumerable<IHarnessHook> GetHooks(RunContext run) =>
        catalog.Config.Hooks.Count == 0 ? [] : [new CommandHook(catalog.Config.Hooks, run.WorkspaceRoot)];
}

internal sealed class CommandHook(IReadOnlyList<CommandHookConfig> hooks, string workspaceRoot) : IHarnessHook
{
    public async ValueTask OnToolCallingAsync(ToolCallingContext c, CancellationToken ct)
    {
        foreach (CommandHookConfig h in Matching("toolCalling", c.ToolName))
        {
            HookOutcome o = await RunAsync(h, Payload(c, "toolCalling", new JsonObject { ["toolName"] = c.ToolName, ["arguments"] = ToNode(c.Arguments) }), ct);
            if (o.Block is { } reason) { c.Block(reason); return; }
            if (o.Stdout?["arguments"] is JsonObject args)
                foreach ((string key, JsonNode? value) in args)
                    c.Arguments[key] = value is null ? null : JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
        }
    }

    public async ValueTask OnToolCalledAsync(ToolCalledContext c, CancellationToken ct)
    {
        foreach (CommandHookConfig h in Matching("toolCalled", c.ToolName))
        {
            HookOutcome o = await RunAsync(h, Payload(c, "toolCalled", new JsonObject
            {
                ["toolName"] = c.ToolName,
                ["arguments"] = ToNode(c.Arguments),
                ["result"] = ToolCallMiddleware.Stringify(c.Result),
            }), ct);
            if (o.Stdout?["result"] is JsonNode result) c.Result = result.GetValueKind() == JsonValueKind.String ? result.GetValue<string>() : result.ToJsonString();
        }
    }

    public async ValueTask OnApprovalRequestedAsync(ApprovalRequestedContext c, CancellationToken ct)
    {
        foreach (CommandHookConfig h in Matching("approvalRequested", c.ToolName))
        {
            HookOutcome o = await RunAsync(h, Payload(c, "approvalRequested", new JsonObject { ["toolName"] = c.ToolName, ["arguments"] = ToNode(c.Arguments) }), ct);
            if (o.Block is { } reason) { c.Deny(reason); return; }
            switch (o.Stdout?["decision"]?.GetValue<string>())
            {
                case "approve": c.Approve(o.Stdout?["reason"]?.GetValue<string>()); return;
                case "deny": c.Deny(o.Stdout?["reason"]?.GetValue<string>() ?? "Denied by hook."); return;
            }
        }
    }

    public async ValueTask OnRunStartingAsync(RunStartingContext c, CancellationToken ct)
    {
        foreach (CommandHookConfig h in Matching("runStarting", null))
        {
            HookOutcome o = await RunAsync(h, Payload(c, "runStarting", new JsonObject { ["messages"] = new JsonArray([.. c.Messages.Select(m => (JsonNode)m.Text)]) }), ct);
            if (o.Block is { } reason) { c.Cancel(reason); return; }
        }
    }

    public async ValueTask OnRunCompletedAsync(RunCompletedContext c, CancellationToken ct)
    {
        foreach (CommandHookConfig h in Matching("runCompleted", null))
            await RunAsync(h, Payload(c, "runCompleted", new JsonObject { ["state"] = c.Result.State, ["text"] = c.Result.Text, ["error"] = c.Result.Error }), ct);
    }

    private IEnumerable<CommandHookConfig> Matching(string evt, string? tool) =>
        hooks.Where(h => string.Equals(h.Event, evt, StringComparison.OrdinalIgnoreCase)
            && (h.Matcher is null || tool is null || Regex.IsMatch(tool, h.Matcher)));

    private static JsonObject Payload(HookContext c, string evt, JsonObject data)
    {
        data["event"] = evt;
        data["runId"] = c.RunId;
        data["sessionId"] = c.SessionId;
        data["agent"] = c.AgentName;
        data["workspace"] = c.WorkspaceRoot;
        return data;
    }

    private sealed record HookOutcome(string? Block, JsonObject? Stdout);

    private async Task<HookOutcome> RunAsync(CommandHookConfig hook, JsonObject payload, CancellationToken ct)
    {
        ProcessStartInfo psi = OperatingSystem.IsWindows()
            ? new("pwsh", ["-NoProfile", "-Command", hook.Command])
            : new("bash", ["-c", hook.Command]);
        psi.WorkingDirectory = workspaceRoot;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        using Process process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync(payload.ToJsonString());
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, hook.TimeoutSeconds)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new HookOutcome($"Hook '{hook.Command}' timed out.", null);
        }
        if (process.ExitCode == 2) return new HookOutcome((await stderr).Trim() is { Length: > 0 } r ? r : "Blocked by hook.", null);
        string output = (await stdout).Trim();
        JsonObject? json = null;
        if (output.StartsWith('{')) try { json = JsonNode.Parse(output) as JsonObject; } catch (JsonException) { }
        return new HookOutcome(null, json);
    }

    private static JsonNode? ToNode(object value) => JsonSerializer.SerializeToNode(value, AIJsonUtilities.DefaultOptions);
}
