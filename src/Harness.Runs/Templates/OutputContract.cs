using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Config;
using Harness.Sdk;
using Json.Schema;
using Microsoft.Extensions.AI;

namespace Harness.Runs.Templates;

/// <summary>
/// The output contract of a templated run. The agent gets one extra tool, <c>submit_output</c>, whose parameter schema is the
/// template's output schema; the run succeeds only when the agent calls it with data that validates and every declared file
/// exists. Each invalid submission, and each turn that ends without a valid one, uses up one retry; the harness tells the agent
/// what is wrong and lets it try again until the retries run out, and the run then ends as <c>invalid_output</c>.
/// </summary>
public sealed class OutputContract
{
    public const string ToolName = "submit_output";

    private readonly TemplateOutput _spec;
    private readonly string _workspace;
    private readonly string _outputDirectory;
    private readonly JsonSchema? _schema;
    private readonly bool _wrapped;
    private readonly Action<JsonObject> _emit;
    private readonly Lock _gate = new();
    private int _failures;
    private bool _submittedThisTurn;
    private List<string> _lastErrors = [];

    /// <param name="outputDirectory"><c>runs/&lt;run-id&gt;/output</c>: accepted output and copies of the declared files go here.</param>
    public OutputContract(RunTemplate template, string workspace, string outputDirectory, Action<JsonObject> emit)
    {
        _spec = template.Output;
        _workspace = workspace;
        _outputDirectory = outputDirectory;
        _emit = emit;

        JsonObject parameters;
        switch (_spec.Kind)
        {
            case "json" when _spec.Schema is { } schemaFile:
                string text = File.ReadAllText(Path.Combine(template.Directory, schemaFile));
                _schema = JsonSchema.FromText(text);
                (parameters, _wrapped) = ToParameters(JsonNode.Parse(text) as JsonObject
                    ?? throw new ConfigException($"Template '{template.Name}': output schema {schemaFile} must be a JSON object."));
                break;
            case "json":
                parameters = new JsonObject { ["type"] = "object", ["description"] = "The run's result as a JSON object." };
                _schema = JsonSchema.FromText(parameters.ToJsonString());
                break;
            case "files":
                parameters = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "One or two sentences on what the files contain." } },
                };
                break;
            default:   // text, reply
                parameters = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string", ["description"] = _spec.Kind == "reply" ? "The reply to send back." : "The run's result." } },
                    ["required"] = new JsonArray("text"),
                };
                break;
        }
        _schema ??= JsonSchema.FromText(parameters.ToJsonString());
        // The OpenAI adapter sends "additionalProperties": false unless the schema says otherwise; keep JSON Schema's default.
        if (!parameters.ContainsKey("additionalProperties")) parameters["additionalProperties"] = true;
        Tool = new SubmitOutputFunction(this, parameters.Deserialize<JsonElement>(), Describe());

        // A run resumed after a restart keeps output it had already submitted.
        if (File.Exists(OutputFile)) Accepted = JsonNode.Parse(File.ReadAllText(OutputFile));
    }

    public AIFunction Tool { get; }

    /// <summary>The validated submission, once there is one.</summary>
    public JsonNode? Accepted { get; private set; }

    public string Kind => _spec.Kind;

    private string OutputFile => Path.Combine(_outputDirectory, "output.json");

    /// <summary>Prompt layer 6: what the run must produce.</summary>
    public string Instructions
    {
        get
        {
            StringBuilder sb = new();
            sb.AppendLine("# Output contract");
            sb.AppendLine("This is an unattended run: nobody reads your messages. When the work is done, call `submit_output` exactly once with " + _spec.Kind switch
            {
                "json" => "the result as JSON that matches the tool's parameter schema.",
                "files" => "a short summary, after writing the files listed below.",
                "reply" => "the reply to send back in `text`.",
                _ => "the result in `text`.",
            });
            if (_spec.Files.Count > 0) sb.AppendLine("These files must exist in the workspace when you submit: " + string.Join(", ", _spec.Files.Select(f => $"`{f}`")) + ".");
            sb.Append("The run succeeds only when `submit_output` accepts your output. If it reports problems, fix them and call it again.");
            return sb.ToString();
        }
    }

    /// <summary>Called at the start of every agent turn.</summary>
    public void BeginTurn()
    {
        lock (_gate) _submittedThisTurn = false;
    }

    /// <summary>
    /// Called when a turn ends. Returns null when the output is accepted, otherwise the message to send the agent for another
    /// try, or throws <see cref="OutputRejectedException"/> when no retries are left.
    /// </summary>
    public string? EndTurn()
    {
        lock (_gate)
        {
            if (Accepted is not null)
            {
                List<string> missing = MissingFiles();
                if (missing.Count == 0)
                {
                    _emit(new JsonObject { ["valid"] = true, ["source"] = "check" });
                    return null;
                }
                Accepted = null;   // the declared files disappeared after the submission
                Fail(missing);
            }
            else if (!_submittedThisTurn) Fail([$"You stopped without calling {ToolName}."]);

            if (_failures > _spec.Retries)
                throw new OutputRejectedException($"No valid output after {_failures} attempt(s): {string.Join(" ", _lastErrors)}");
            int left = _spec.Retries - _failures;
            return $"""
                The run is not finished: its output contract is not met.
                {string.Join("\n", _lastErrors.Select(e => "- " + e))}
                Fix this and call {ToolName}. {(left == 0 ? "This is the last attempt." : $"Attempts left after this one: {left}.")}
                """;
        }
    }

    private void Fail(List<string> errors)
    {
        _failures++;
        _lastErrors = errors;
        _emit(new JsonObject { ["valid"] = false, ["source"] = "check", ["errors"] = new JsonArray([.. errors.Select(e => (JsonNode)e)]), ["attempt"] = _failures });
    }

    /// <summary>The body of <c>submit_output</c>.</summary>
    internal string Submit(JsonObject arguments)
    {
        lock (_gate)
        {
            _submittedThisTurn = true;
            List<string> errors = Validate(arguments);
            errors.AddRange(MissingFiles());
            if (errors.Count == 0)
            {
                Accepted = _wrapped ? arguments["output"]?.DeepClone() : arguments.DeepClone();
                Directory.CreateDirectory(_outputDirectory);
                File.WriteAllText(OutputFile, Accepted?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
                _emit(new JsonObject { ["valid"] = true, ["source"] = ToolName });
                return "Output accepted. The run is complete: reply with one short sentence and stop.";
            }
            _failures++;
            _lastErrors = errors;
            _emit(new JsonObject { ["valid"] = false, ["source"] = ToolName, ["errors"] = new JsonArray([.. errors.Select(e => (JsonNode)e)]), ["attempt"] = _failures });
            int left = _spec.Retries - _failures;
            string header = left >= 0
                ? $"Output rejected · {errors.Count} problem(s) · attempts left: {left + 1}. Fix them and call {ToolName} again."
                : "Output rejected · no attempts left. Stop now.";
            return header + "\n" + string.Join("\n", errors.Select(e => "- " + e));
        }
    }

    private List<string> Validate(JsonObject arguments)
    {
        JsonElement instance = arguments.Deserialize<JsonElement>();
        if (_wrapped)
        {
            if (!arguments.TryGetPropertyValue("output", out JsonNode? inner)) return ["Pass the result in the `output` parameter."];
            instance = JsonSerializer.SerializeToElement(inner);
        }
        EvaluationResults results = _schema!.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, IncludeApplicatorErrors = false });
        if (results.IsValid) return [];
        List<string> errors = [];
        foreach (EvaluationResults detail in (results.Details ?? []).Prepend(results))
            foreach ((string _, string message) in detail.Errors ?? new Dictionary<string, string>())
            {
                string at = detail.InstanceLocation.ToString();
                if (_wrapped) at = "/output" + at;
                errors.Add($"{(at.Length == 0 ? "(root)" : at)}: {message}");
            }
        return errors.Count > 0 ? [.. errors.Distinct()] : ["The output does not match the schema."];
    }

    private List<string> MissingFiles() =>
        [.. _spec.Files.Where(f => !File.Exists(Path.Combine(_workspace, f))).Select(f => $"Required file `{f}` does not exist in the workspace.")];

    /// <summary>Copies the declared files to <c>runs/&lt;run-id&gt;/output/files</c>, so they outlive the workspace, and returns the copies.</summary>
    public IReadOnlyList<string> CollectFiles()
    {
        List<string> copies = [];
        foreach (string file in _spec.Files)
        {
            string source = Path.GetFullPath(Path.Combine(_workspace, file));
            if (!File.Exists(source) || !WorkspacePaths.IsInside(_workspace, source)) continue;
            string target = Path.Combine(_outputDirectory, "files", Path.GetRelativePath(_workspace, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            copies.Add(target);
        }
        return copies;
    }

    private string Describe() => _spec.Kind switch
    {
        "json" => "Submit the run's result. The run succeeds only when this is called with data that matches the schema.",
        "files" => "Submit the run's result after writing the required files. The run succeeds only when they exist.",
        _ => "Submit the run's result. The run succeeds only when this is called.",
    };

    /// <summary>Tool parameters must be an object; any other schema is wrapped as <c>{ output: schema }</c>.</summary>
    private static (JsonObject Parameters, bool Wrapped) ToParameters(JsonObject schema)
    {
        JsonObject copy = (JsonObject)schema.DeepClone();
        copy.Remove("$schema");
        copy.Remove("$id");
        if (copy["type"] is JsonValue t && t.ToString() == "object") return (copy, false);
        JsonObject wrapper = new() { ["type"] = "object", ["required"] = new JsonArray("output") };
        foreach (string defs in new[] { "$defs", "definitions" })
            if (copy.Remove(defs, out JsonNode? d)) wrapper[defs] = d;
        wrapper["properties"] = new JsonObject { ["output"] = copy };
        return (wrapper, true);
    }

    private sealed class SubmitOutputFunction(OutputContract contract, JsonElement schema, string description) : AIFunction
    {
        public override string Name => ToolName;
        public override string Description => description;
        public override JsonElement JsonSchema => schema;
        // Strict mode would rewrite the schema (every property required, no extra properties), which changes the contract;
        // the harness validates against the template's schema itself.
        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } = new Dictionary<string, object?> { ["strict"] = false };

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            JsonObject args = new();
            foreach ((string key, object? value) in arguments)
                args[key] = value is null ? null : JsonSerializer.SerializeToNode(value, AIJsonUtilities.DefaultOptions);
            return ValueTask.FromResult<object?>(contract.Submit(args));
        }
    }
}

/// <summary>The agent used up its retries without meeting the output contract.</summary>
public sealed class OutputRejectedException(string message) : Exception(message);
