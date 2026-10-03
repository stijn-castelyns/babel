using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Harness.Core.Config;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace Harness.Core.Models;

/// <summary>
/// Builds an <see cref="IChatClient"/> for a model profile. Everything goes through Chat Completions:
/// nothing above this layer may depend on Responses-API features.
/// </summary>
public sealed class ChatClientFactory(SecretStore secrets)
{
    public IChatClient Create(ModelProfile profile)
    {
        IChatClient client = profile.Provider switch
        {
            "ollama" => new OllamaApiClient(new Uri(Require(profile.Endpoint, "endpoint")), Require(profile.Model, "model")),
            "azure-openai" => CreateAzure(profile),
            "openai" => CreateOpenAI(profile),
            _ => throw new NotSupportedException($"Model provider '{profile.Provider}' is not supported. Use ollama, azure-openai or openai."),
        };
        EnsureChatCompletions(client, profile);
        return client;
    }

    private static readonly Type? ResponsesClientType = Type.GetType("OpenAI.Responses.ResponsesClient, OpenAI");

    /// <summary>Rejects any client that is backed by the Responses API, so the chat-completions rule cannot regress.</summary>
    public static void EnsureChatCompletions(IChatClient client, ModelProfile profile)
    {
        if ((ResponsesClientType is not null && client.GetService(ResponsesClientType) is not null)
            || client.GetType().FullName?.Contains("Responses", StringComparison.Ordinal) == true)
            throw new InvalidOperationException($"Model profile for '{profile.Endpoint}' resolved to a Responses API client; only Chat Completions is allowed.");
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

    private static string Require(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ConfigException($"Model profile is missing '{field}'.") : value;
}
