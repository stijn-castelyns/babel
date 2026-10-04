using Harness.Core;

namespace Harness.Cli;

/// <summary>Starter files written by <c>harness init</c>.</summary>
internal static class Samples
{
    public static IEnumerable<(string File, string Content)> Files(HarnessPaths paths)
    {
        yield return (paths.ConfigFile, """
            # Model profiles. Every provider goes through Chat Completions.
            models:
              local:
                provider: ollama
                endpoint: http://localhost:11434
                model: your-coding-model      # an Ollama tag; create it from a Modelfile with PARAMETER num_ctx 32768 or more
                maxConcurrency: 1             # one GPU: one run at a time
              # azure:
              #   provider: azure-openai
              #   endpoint: https://<resource>.openai.azure.com
              #   deployment: <chat-deployment>
              #   auth: { type: entra }       # or { type: apiKey, secret: secret:azure-openai-key }

            defaults:
              agent: coder

            listeners:
              # api: http://127.0.0.1:7443      # opt in; put Tailscale Serve in front for HTTPS
              # publicHost: box.tailnet.ts.net  # the name the web app is reached at; passkeys are bound to it
              # behindProxy: true               # take X-Forwarded-Proto/-For from that proxy on this machine
              # apiToken: secret:api-token
              # webhooks: http://127.0.0.1:7444 # expose only this one, through a tunnel

            # Where 'harness update' finds releases. Release builds already know; a private repository needs a token.
            # update:
            #   token: secret:github-releases

            # Command hooks: the hook context arrives as JSON on stdin; exit 2 blocks with stderr as the reason.
            hooks: []
            #  - event: toolCalling
            #    matcher: ^shell$
            #    command: ~/.harness/hooks/no-force-push.sh
            """);

        yield return (Path.Combine(paths.AgentsDir, "coder.yaml"), """
            name: coder
            description: General coding agent for the current workspace.
            model: local
            instructions: prompts/coder.md
            tools:
              builtin: [read, list, glob, grep, edit, write, shell]
              mcp: []
            skills: [skills/]
            approvals: { shell: ask, write: allow, edit: allow, mcp: ask }
            sandbox: none                       # interactive work on your own repo; use 'workspace' for isolation
            compaction: { toolResultsAfter: 40, slidingWindowTurns: 30 }
            limits: { maxToolIterations: 60, maxRunMinutes: 30 }
            """);

        yield return (Path.Combine(paths.PromptsDir, "coder.md"), """
            Work in small, verifiable steps. Read the relevant code before changing it, keep changes minimal,
            and run the project's tests or build after editing. When something is ambiguous, say what you assumed.
            """);

        yield return (paths.SandboxesFile, """
            # Sandbox profiles, referenced by name from agents and triggers.
            workspace:
              type: bubblewrap
              network: none                     # none | allowlist (via the egress proxy, with allowHosts) | full
              mounts:
                - { host: "{workspace}", path: /workspace, mode: rw }
              env: { DOTNET_CLI_TELEMETRY_OPTOUT: "1" }
              limits: { wallClockMinutes: 30 }

            dotnet-ci:
              type: container
              image: mcr.microsoft.com/dotnet/sdk:10.0
              network: none
              mounts: [{ host: "{workspace}", path: /workspace, mode: rw }]
              limits: { cpus: 2, memoryMb: 4096, pids: 256 }
            """);

        yield return (paths.GlobalAgentsMd, """
            <!-- Instructions here apply to every run, for every agent. -->
            """);

        yield return (paths.McpFile, """
            {
              "mcpServers": {}
            }
            """);
    }
}
