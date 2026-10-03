using System.Diagnostics;
using Harness.Sdk;

namespace Harness.Sandbox;

/// <summary>How commands are wrapped: <c>bash -lc</c> on Linux and macOS, <c>pwsh -NoProfile -Command</c> on Windows.</summary>
public static class Shells
{
    public static IReadOnlyList<string> CommandLine(ExecRequest request, bool windows) =>
        request.Arguments is { } args
            ? [request.Command, .. args]
            : windows ? ["pwsh", "-NoProfile", "-Command", request.Command] : ["bash", "-lc", request.Command];

    public static ProcessStartInfo StartInfo(IReadOnlyList<string> argv, string? workingDirectory)
    {
        ProcessStartInfo psi = new(argv[0]);
        foreach (string a in argv.Skip(1)) psi.ArgumentList.Add(a);
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        return psi;
    }
}
