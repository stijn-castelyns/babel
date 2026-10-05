using System.Text.Json.Nodes;
using Harness.Core;
using Harness.Core.Agents;
using Harness.Core.Config;
using Harness.Core.Models;
using Harness.Core.Sessions;
using Harness.Runs;
using Harness.Sdk;
using Harness.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable OPENAI001 // Responses types are evaluation-only in the OpenAI SDK

namespace Harness.Tests;

public class ResponsesApiTests
{
    [Fact]
    public void Responses_is_opt_in_and_rejected_where_unsupported()
    {
        Assert.False(ChatClientFactory.UsesResponses(new ModelProfile()));
        Assert.True(ChatClientFactory.UsesResponses(new ModelProfile { Api = "responses" }));
        Assert.Throws<ConfigException>(() => ChatClientFactory.UsesResponses(new ModelProfile { Api = "assistants" }));

        ChatClientFactory factory = new(new SecretStore(new HarnessPaths(Path.GetTempPath())));
        Assert.Throws<ConfigException>(() => factory.Create(new ModelProfile { Provider = "ollama", Endpoint = "http://localhost:11434", Model = "m", Api = "responses" }));

        ModelProfile responses = new() { Provider = "openai", Endpoint = "http://127.0.0.1:9/v1", Model = "m", Api = "responses" };
        using IChatClient client = factory.Create(responses);
        Assert.NotNull(client.GetService<StatelessResponsesChatClient>());

        // The same client under a chat-completions profile, or a Responses client that is not stateless, is refused.
        Assert.Throws<InvalidOperationException>(() => ChatClientFactory.EnsureApi(client, new ModelProfile { Endpoint = "x" }));
        IChatClient bare = client.GetService<StatelessResponsesChatClient>()!.GetService<OpenAI.Responses.ResponsesClient>() is { } raw
            ? Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(raw, "m")
            : throw new InvalidOperationException("no ResponsesClient");
        Assert.Throws<InvalidOperationException>(() => ChatClientFactory.EnsureApi(bare, responses));
    }

    [Theory]
    [InlineData("https://res.openai.azure.com", "https://res.openai.azure.com/openai/v1/")]
    [InlineData("https://res.openai.azure.com/", "https://res.openai.azure.com/openai/v1/")]
    [InlineData("https://res.services.ai.azure.com/api/projects/proj-default", "https://res.services.ai.azure.com/openai/v1/")]
    [InlineData("https://res.openai.azure.com/openai/v1", "https://res.openai.azure.com/openai/v1/")]
    [InlineData("https://gateway.example.com/aoai", "https://gateway.example.com/aoai/openai/v1/")]
    public void Azure_endpoints_map_to_the_v1_surface(string endpoint, string expected) =>
        Assert.Equal(expected, ChatClientFactory.AzureV1Endpoint(endpoint).ToString());

    [Fact]
    public void Requests_never_continue_server_side_state()
    {
        Assert.Throws<InvalidOperationException>(() => StatelessResponsesChatClient.Stateless(new ChatOptions { ConversationId = "resp_1" }));
    }

    /// <summary>
    /// A real run through Agent Framework against a fake Responses endpoint: every request must be stateless, and after a
    /// daemon restart the next turn must replay the whole conversation from local history, encrypted reasoning included.
    /// </summary>
    [Fact]
    public async Task Runs_replay_local_history_with_store_disabled()
    {
        List<JsonObject> requests = [];
        await using WebApplication fake = FakeResponses(requests);
        await fake.StartAsync();
        string url = fake.Urls.First();

        await using TestHome home = new();
        File.WriteAllText(home.Paths.ConfigFile, $"""
            models:
              fake:
                provider: openai
                api: responses
                endpoint: {url}/v1
                model: fake-model
            defaults:
              agent: coder
            """);
        File.WriteAllText(Path.Combine(home.Workspace, "notes.txt"), "alpha\n");

        home.Build(new ScriptedChatClient((_, _) => throw new InvalidOperationException("override must not be used")));
        home.Services!.GetRequiredService<AgentFactory>().ChatClientOverride = null;
        SessionFolder session = home.Orchestrator.CreateSession(new SessionRequest { Workspace = home.Workspace });
        RunRecord first = home.Orchestrator.Start(new RunRequest { SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, "first")] });
        RunResult firstResult = await home.Orchestrator.WaitAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(firstResult.State == RunStates.Succeeded, firstResult.Error);
        Assert.Equal("done", firstResult.Text);

        // Restart: the second turn can only know about the first one from the session files on disk.
        await home.Services!.DisposeAsync();
        home.Build(new ScriptedChatClient((_, _) => throw new InvalidOperationException("override must not be used")));
        home.Services!.GetRequiredService<AgentFactory>().ChatClientOverride = null;
        RunRecord second = home.Orchestrator.Start(new RunRequest { SessionId = session.Id, Messages = [new ChatMessage(ChatRole.User, "again")] });
        RunResult secondResult = await home.Orchestrator.WaitAsync(second.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(secondResult.State == RunStates.Succeeded, secondResult.Error);
        Assert.Equal("second reply", secondResult.Text);

        Assert.Equal(3, requests.Count);
        foreach (JsonObject request in requests)
        {
            Assert.False(request["store"]!.GetValue<bool>());
            Assert.Contains("reasoning.encrypted_content", request["include"]!.AsArray().Select(i => i!.GetValue<string>()));
            Assert.Null(request["previous_response_id"]);
            Assert.Null(request["conversation"]);
        }

        string[] replayed = [.. requests[2]["input"]!.AsArray().Select(Describe)];
        Assert.Equal(["user:first", "reasoning:rs_1:enc-1", "function_call:call_1", "function_call_output:call_1", "assistant:done", "user:again"],
            replayed.Where(r => !r.StartsWith("system:", StringComparison.Ordinal) && !r.StartsWith("developer:", StringComparison.Ordinal)));
    }

    private static string Describe(JsonNode? item)
    {
        string type = item?["type"]?.GetValue<string>() ?? "message";
        return type switch
        {
            "message" => $"{item!["role"]!.GetValue<string>()}:{Text(item["content"])}",
            "reasoning" => $"reasoning:{item!["id"]?.GetValue<string>()}:{item["encrypted_content"]?.GetValue<string>()}",
            "function_call" or "function_call_output" => $"{type}:{item!["call_id"]!.GetValue<string>()}",
            _ => type,
        };
    }

    private static string Text(JsonNode? content) => content switch
    {
        JsonValue v => v.GetValue<string>(),
        JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>())),
        _ => "",
    };

    /// <summary>A scripted streaming Responses endpoint, modelled on what Azure OpenAI sends for <c>store: false</c>.</summary>
    private static WebApplication FakeResponses(List<JsonObject> requests)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.MapPost("/v1/responses", async (HttpContext http) =>
        {
            JsonObject request = (await JsonNode.ParseAsync(http.Request.Body))!.AsObject();
            lock (requests) requests.Add(request);
            JsonArray input = request["input"]!.AsArray();
            string last = Describe(input[^1]);

            JsonArray output = last == "user:first"
                ? [
                    new JsonObject { ["id"] = "rs_1", ["type"] = "reasoning", ["summary"] = new JsonArray(), ["encrypted_content"] = "enc-1" },
                    new JsonObject { ["id"] = "fc_1", ["type"] = "function_call", ["status"] = "completed", ["call_id"] = "call_1", ["name"] = "read", ["arguments"] = """{"path":"notes.txt"}""" },
                ]
                : [Message(last.StartsWith("function_call_output:", StringComparison.Ordinal) ? "done" : "second reply")];

            http.Response.ContentType = "text/event-stream";
            int seq = 0;
            async Task Send(string type, JsonObject data)
            {
                data["type"] = type;
                data["sequence_number"] = seq++;
                await http.Response.WriteAsync($"event: {type}\ndata: {data.ToJsonString()}\n\n");
                await http.Response.Body.FlushAsync();
            }
            await Send("response.created", new JsonObject { ["response"] = Response("in_progress", []) });
            for (int i = 0; i < output.Count; i++)
            {
                JsonNode item = output[i]!;
                await Send("response.output_item.added", new JsonObject { ["output_index"] = i, ["item"] = item.DeepClone() });
                if (item["type"]!.GetValue<string>() == "message")
                    await Send("response.output_text.delta", new JsonObject { ["item_id"] = item["id"]!.GetValue<string>(), ["output_index"] = i, ["content_index"] = 0, ["delta"] = Text(item["content"]) });
                await Send("response.output_item.done", new JsonObject { ["output_index"] = i, ["item"] = item.DeepClone() });
            }
            await Send("response.completed", new JsonObject { ["response"] = Response("completed", output) });
        });
        return app;

        static JsonObject Message(string text) => new()
        {
            ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..8], ["type"] = "message", ["status"] = "completed", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() }),
        };

        static JsonObject Response(string status, JsonArray output) => new()
        {
            ["id"] = "resp_" + Guid.NewGuid().ToString("N")[..8], ["object"] = "response", ["created_at"] = 0, ["status"] = status,
            ["model"] = "fake-model", ["output"] = output.DeepClone(), ["store"] = false, ["parallel_tool_calls"] = true, ["tools"] = new JsonArray(),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 100, ["output_tokens"] = 10, ["total_tokens"] = 110,
                ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 5 },
            },
        };
    }
}
