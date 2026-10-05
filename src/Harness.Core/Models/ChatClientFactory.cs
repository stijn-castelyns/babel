using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Identity;
using Harness.Core.Config;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using OpenAI.Responses;

// The OpenAI SDK still marks its Responses types as evaluation-only; they are used here and nowhere else.
#pragma warning disable OPENAI001

namespace Harness.Core.Models;

/// <summary>
/// Builds an <see cref="IChatClient"/> for a model profile. Everything goes through Chat Completions unless a profile opts
/// into <c>api: responses</c>, which is always stateless (<see cref="StatelessResponsesChatClient"/>): history stays in the
/// harness's session files either way, and nothing above this layer may depend on Responses-API features.
/// </summary>
public sealed class ChatClientFactory(SecretStore secrets)
{
    public IChatClient Create(ModelProfile profile)
    {
        IChatClient client = (profile.Provider, UsesResponses(profile)) switch
        {
            ("ollama", false) => new OllamaApiClient(new Uri(Require(profile.Endpoint, "endpoint")), Require(profile.Model, "model")),
            ("azure-openai", false) => CreateAzure(profile),
            ("openai", false) => CreateOpenAI(profile),
            ("azure-openai", true) => new StatelessResponsesChatClient(CreateAzureResponses(profile)),
            ("openai", true) => new StatelessResponsesChatClient(CreateOpenAIResponses(profile)),
            ("ollama", true) => throw new ConfigException("Model profile sets 'api: responses', which only azure-openai and openai support."),
            _ => throw new NotSupportedException($"Model provider '{profile.Provider}' is not supported. Use ollama, azure-openai or openai."),
        };
        EnsureApi(client, profile);
        return client;
    }

    /// <summary>True when the profile opts into the Responses API (<c>api: responses</c>); the default is Chat Completions.</summary>
    public static bool UsesResponses(ModelProfile profile) => profile.Api switch
    {
        null or "" or "chatCompletions" => false,
        "responses" => true,
        var other => throw new ConfigException($"Model profile has api '{other}'. Use chatCompletions or responses."),
    };

    private static readonly Type? ResponsesClientType = Type.GetType("OpenAI.Responses.ResponsesClient, OpenAI");

    /// <summary>
    /// Rejects a Responses API client unless the profile opted in, and then accepts only a stateless one, so neither the
    /// chat-completions default nor the local-history rule can regress.
    /// </summary>
    public static void EnsureApi(IChatClient client, ModelProfile profile)
    {
        bool responses = (ResponsesClientType is not null && client.GetService(ResponsesClientType) is not null)
            || client.GetType().FullName?.Contains("Responses", StringComparison.Ordinal) == true;
        if (!responses) return;
        if (!UsesResponses(profile))
            throw new InvalidOperationException($"Model profile for '{profile.Endpoint}' resolved to a Responses API client; only Chat Completions is allowed unless the profile sets 'api: responses'.");
        if (client.GetService<StatelessResponsesChatClient>() is null)
            throw new InvalidOperationException($"Model profile for '{profile.Endpoint}' resolved to a Responses API client that may store history server-side.");
    }

    private IChatClient CreateAzure(ModelProfile p)
    {
        Uri endpoint = new(Require(p.Endpoint, "endpoint"));
        string deployment = Require(p.Deployment ?? p.Model, "deployment");
        AzureOpenAIClient azure = (p.Auth?.Type ?? "entra") switch
        {
            "entra" => new AzureOpenAIClient(endpoint, new DefaultAzureCredential()),
            "apiKey" => new AzureOpenAIClient(endpoint, new ApiKeyCredential(secrets.Require(p.Auth!.Secret ?? "", "azure-openai apiKey"))),
            var other => throw new NotSupportedException($"Auth type '{other}' is not supported for azure-openai. Use entra or apiKey."),
        };
        return azure.GetChatClient(deployment).AsIChatClient();   // Chat Completions only, never GetResponsesClient
    }

    private IChatClient CreateOpenAI(ModelProfile p)
    {
        string key = p.Auth is { Type: "apiKey", Secret: { } secret } ? secrets.Require(secret, "openai apiKey") : "unused";
        OpenAIClientOptions options = new();
        if (!string.IsNullOrEmpty(p.Endpoint)) options.Endpoint = new Uri(p.Endpoint);
        return new OpenAIClient(new ApiKeyCredential(key), options).GetChatClient(Require(p.Model, "model")).AsIChatClient();
    }

    private IChatClient CreateAzureResponses(ModelProfile p)
    {
        ResponsesClientOptions options = new() { Endpoint = AzureV1Endpoint(Require(p.Endpoint, "endpoint")) };
        string deployment = Require(p.Deployment ?? p.Model, "deployment");
        ResponsesClient client = (p.Auth?.Type ?? "entra") switch
        {
            "entra" => new ResponsesClient(new BearerTokenPolicy(new DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default"), options),
            "apiKey" => new ResponsesClient(new ApiKeyCredential(secrets.Require(p.Auth!.Secret ?? "", "azure-openai apiKey")), options),
            var other => throw new NotSupportedException($"Auth type '{other}' is not supported for azure-openai. Use entra or apiKey."),
        };
        return client.AsIChatClient(deployment);
    }

    private IChatClient CreateOpenAIResponses(ModelProfile p)
    {
        string key = p.Auth is { Type: "apiKey", Secret: { } secret } ? secrets.Require(secret, "openai apiKey") : "unused";
        ResponsesClientOptions options = new();
        if (!string.IsNullOrEmpty(p.Endpoint)) options.Endpoint = new Uri(p.Endpoint);
        return new ResponsesClient(new ApiKeyCredential(key), options).AsIChatClient(Require(p.Model, "model"));
    }

    /// <summary>
    /// The Responses API lives on Azure's v1 surface (<c>/openai/v1/</c>). Accepts the resource endpoint, a Foundry project
    /// endpoint (<c>…/api/projects/&lt;name&gt;</c>) or the v1 URL itself.
    /// </summary>
    internal static Uri AzureV1Endpoint(string endpoint)
    {
        Uri uri = new(endpoint);
        string root = uri.GetLeftPart(UriPartial.Authority);
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase)) return new Uri(root + path + "/");
        if (path.Length == 0 || path.StartsWith("/api/projects", StringComparison.OrdinalIgnoreCase)) return new Uri(root + "/openai/v1/");
        return new Uri(root + path + "/openai/v1/");
    }

    private static string Require(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ConfigException($"Model profile is missing '{field}'.") : value;
}
