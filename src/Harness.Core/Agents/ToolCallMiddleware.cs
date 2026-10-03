using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Prompts;
using Harness.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Harness.Core.Agents;

/// <summary>
/// Function-invocation middleware around every tool call (built-in, MCP, skill or plugin): emits tool events,
/// runs <c>OnToolCalling</c>/<c>OnToolCalled</c> hooks, enforces <c>maxToolIterations</c>, masks secrets,
/// spills oversized results, and appends lazy folder instructions (prompt layer 5).
/// </summary>
public sealed class ToolCallMiddleware(RunContext run, HookPipeline hooks)
{
    /// <summary>Tools whose <c>path</c> argument triggers lazy folder instructions.</summary>
    private static readonly HashSet<string> FolderAwareTools = ["read", "edit", "list", "write"];
    private const int EventResultChars = 2_000;

    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        string name = context.Function.Name;
        string callId = context.CallContent?.CallId ?? Ids.New("call_");
        IDictionary<string, object?> arguments = context.Arguments;
        int count = run.IncrementToolCalls();

        run.Events.Emit(EventTypes.ToolCallStart, new JsonObject
        {
            ["toolCallId"] = callId,
            ["toolName"] = name,
            ["arguments"] = ToJson(arguments),
        });

        string result;
        bool ok = true;
        if (count > run.Agent.Limits.MaxToolIterations)
        {
            context.Terminate = true;
            ok = false;
            result = $"Error: the run reached its limit of {run.Agent.Limits.MaxToolIterations} tool calls. Stop and summarise progress.";
        }
        else
        {
            ToolCallingContext calling = new()
            {
                RunId = run.RunId, SessionId = run.SessionId, AgentName = run.AgentName, WorkspaceRoot = run.WorkspaceRoot,
                ToolName = name, CallId = callId, Arguments = arguments,
            };
            await hooks.ToolCallingAsync(calling, cancellationToken);
            if (calling.Blocked)
            {
                ok = false;
                result = $"Blocked: {calling.BlockReason}";
            }
            else
            {
                object? raw;
                Exception? error = null;
                try
                {
                    raw = await next(context, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex;
                    raw = $"Error: {ex.Message}";
                    ok = false;
                }

                ToolCalledContext called = new()
                {
                    RunId = run.RunId, SessionId = run.SessionId, AgentName = run.AgentName, WorkspaceRoot = run.WorkspaceRoot,
                    ToolName = name, CallId = callId, Arguments = arguments.AsReadOnly(), Result = raw, Exception = error,
                };
                await hooks.ToolCalledAsync(called, cancellationToken);
                result = run.Mask(Stringify(called.Result));
                result = SpillStore.Fit(result, run.SpillThresholdChars, run.WorkspaceRoot, name);
                if (ok && FolderAwareTools.Contains(name))
                    result += LazyFolderInstructions(arguments);
            }
        }

        string? main = ApprovalPolicy.MainArgument(name, arguments.AsReadOnly());
        run.LastTool = main is null ? name : $"{name}: {Truncate(main, 80)}";
        run.Events.Emit(EventTypes.ToolCallEnd, new JsonObject { ["toolCallId"] = callId, ["toolName"] = name, ["ok"] = ok });
        run.Events.Emit(EventTypes.ToolCallResult, new JsonObject
        {
            ["toolCallId"] = callId,
            ["toolName"] = name,
            ["content"] = Truncate(result, EventResultChars),
            ["truncated"] = result.Length > EventResultChars,
        });
        return result;
    }

    /// <summary>Instructions from folders between the workspace root and the touched path that this session has not seen yet.</summary>
    private string LazyFolderInstructions(IDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("path", out object? value) || value is null) return "";
        string path = value is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : value.ToString() ?? "";
        string full;
        try { full = Path.GetFullPath(Path.Combine(run.WorkspaceRoot, path)); }
        catch (Exception) { return ""; }
        string root = Path.GetFullPath(run.WorkspaceRoot);
        string? dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        if (dir is null || !(dir == root || dir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            return "";

        System.Text.StringBuilder sb = new();
        foreach ((string file, string text) in PromptComposer.FolderChain(root, dir, run.FolderInstructionFiles))
        {
            string folder = Path.GetDirectoryName(file)!;
            if (!run.State.LoadedFolderInstructions.TryAdd(folder, true)) continue;
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            string scope = Path.GetDirectoryName(rel) is { Length: > 0 } d ? d.Replace('\\', '/') : ".";
            sb.Append($"\n\n[Instructions from {rel}; they apply to work in {scope}/]\n").Append(text.Trim());
        }
        return sb.ToString();
    }

    public static string Stringify(object? value) => value switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        _ => JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions),
    };

    private static JsonNode? ToJson(IDictionary<string, object?> arguments)
    {
        try { return JsonSerializer.SerializeToNode(arguments, AIJsonUtilities.DefaultOptions); }
        catch (Exception) { return new JsonObject(); }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
