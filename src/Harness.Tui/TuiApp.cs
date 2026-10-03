using System.Diagnostics;
using Harness.Client;
using Harness.Tui.Views;
using Terminal.Gui.App;

namespace Harness.Tui;

/// <summary>Runs the full-screen TUI against a daemon. A pure client: everything goes through <see cref="HarnessClient"/>.</summary>
public static class TuiApp
{
    /// <summary>
    /// Whether a full-screen UI can run here: both ends are a terminal and it is not a dumb one. Otherwise (piped
    /// output, <c>--plain</c>, <c>TERM=dumb</c>) the commands print line-oriented output instead.
    /// </summary>
    public static bool CanRun(bool plain)
    {
        if (plain || Console.IsOutputRedirected || Console.IsInputRedirected) return false;
        string? term = Environment.GetEnvironmentVariable("TERM");
        if (OperatingSystem.IsWindows()) return term != "dumb";
        return term is { Length: > 0 } && term != "dumb";
    }

    public static async Task<int> RunAsync(HarnessClient client, TuiOptions options, CancellationToken ct)
    {
        Dispatcher ui = new();
        await using TuiController controller = new(client, options, ui.Post);
        // Connect before taking over the screen, so "daemon not running" is an ordinary error message.
        await controller.StartAsync();

        Theme theme = Theme.Create(options.Theme);
        string? composerText = null;
        while (!ct.IsCancellationRequested)
        {
            MainWindow window = new(controller, theme);
            string? editorRequest;
            using (IApplication app = Application.Create())
            {
                app.Init();
                if (composerText is not null) window.ComposerText = composerText;
                app.Keyboard.KeyDown += (_, key) =>
                {
                    if (window.Dispatch(key)) key.Handled = true;
                };
                app.ScreenChanged += (_, _) => window.Relayout();
                // Clocks, "ago" columns and flash messages age once a second.
                app.AddTimeout(TimeSpan.FromSeconds(1), () =>
                {
                    window.Refresh();
                    return true;
                });
                app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
                {
                    window.FocusInitial();
                    return false;
                });
                using CancellationTokenRegistration stop = ct.Register(() => app.Invoke(() => app.RequestStop()));
                ui.Attach(app);
                try { app.Run(window); }
                finally { ui.Detach(); }
                editorRequest = window.EditorRequest;
                window.Dispose();
            }
            if (editorRequest is null || controller.QuitRequested) break;
            composerText = Edit(editorRequest);
        }
        return 0;
    }

    /// <summary>Ctrl+G: the screen is handed back to the terminal while <c>$VISUAL</c> / <c>$EDITOR</c> edits the message.</summary>
    private static string Edit(string text)
    {
        string file = Path.Combine(Path.GetTempPath(), $"harness-message-{Environment.ProcessId}.md");
        File.WriteAllText(file, text);
        string editor = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR") ?? (OperatingSystem.IsWindows() ? "notepad" : "vi");
        try
        {
            string[] parts = editor.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ProcessStartInfo start = new(parts[0]) { UseShellExecute = false };
            foreach (string arg in parts.Skip(1)) start.ArgumentList.Add(arg);
            start.ArgumentList.Add(file);
            using Process? p = Process.Start(start);
            p?.WaitForExit();
            return File.ReadAllText(file).TrimEnd('\n', '\r');
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return text;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Marshals controller updates onto the UI thread. While no UI is up (start-up, the editor) they run at once, one at
    /// a time under a lock.
    /// </summary>
    private sealed class Dispatcher
    {
        private readonly Lock _gate = new();
        private IApplication? _app;

        public void Post(Action action)
        {
            IApplication? app;
            lock (_gate) app = _app;
            if (app is not null)
            {
                app.Invoke(action);
                return;
            }
            lock (_gate) action();
        }

        public void Attach(IApplication app)
        {
            lock (_gate) _app = app;
        }

        public void Detach()
        {
            lock (_gate) _app = null;
        }
    }
}
