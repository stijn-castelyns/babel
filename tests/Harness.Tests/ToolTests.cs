using Harness.Core.Agents;
using Harness.Tools;

namespace Harness.Tests;

public sealed class ToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-tools-").FullName;
    private readonly SessionRuntimeState _state = new();
    private readonly FileTools _tools;

    public ToolTests()
    {
        _tools = new FileTools(new Workspace(_root), _state);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "class A\n{\n    int x = 1;\n}\n");
        File.WriteAllText(Path.Combine(_root, "src", "b.cs"), "class B { }\n");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\n*.log\n");
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "out.dll"), "x");
        File.WriteAllText(Path.Combine(_root, "debug.log"), "x");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Read_pages_with_a_header_and_reports_unchanged_rereads()
    {
        File.WriteAllLines(Path.Combine(_root, "long.txt"), Enumerable.Range(1, 1000).Select(i => $"line {i}"));
        string first = await _tools.Read("long.txt", 1, 400);
        Assert.StartsWith("long.txt · lines 1–400 of 1,000 · next: offset=401", first);
        Assert.Contains("400\tline 400", first);
        Assert.DoesNotContain("line 401", first);
        string again = await _tools.Read("long.txt", 1, 400);
        Assert.Contains("unchanged since turn", again);
        string last = await _tools.Read("long.txt", 901, 400);
        Assert.Contains("end of file", last);
    }

    [Fact]
    public async Task Edit_requires_a_fresh_read_and_a_unique_match()
    {
        Assert.StartsWith("Error: read src/a.cs before", await _tools.Edit("src/a.cs", "int x = 1;", "int x = 2;"));
        await _tools.Read("src/a.cs");
        string edited = await _tools.Edit("src/a.cs", "int x = 1;", "int x = 2;");
        Assert.StartsWith("edited src/a.cs · 1 replacement · at line 3", edited);
        Assert.Equal("""
            edited src/a.cs · 1 replacement · at line 3
              {
            -     int x = 1;
            +     int x = 2;
              }

            """, edited);
        // The tool remembers its own write, so a second edit needs no re-read...
        Assert.StartsWith("edited", await _tools.Edit("src/a.cs", "int x = 2;", "int x = 3;"));
        // ...but a change made by someone else does.
        File.AppendAllText(Path.Combine(_root, "src", "a.cs"), "// external\n");
        Assert.Contains("changed since you last read it", await _tools.Edit("src/a.cs", "int x = 3;", "int x = 4;"));

        await _tools.Read("src/a.cs");
        string dup = await _tools.Edit("src/a.cs", "x", "y");
        Assert.True(dup.Contains("occurs"), dup);
        Assert.Contains("was not found", await _tools.Edit("src/a.cs", "missing", "y"));
    }

    [Fact]
    public async Task Write_creates_files_but_will_not_clobber_unread_ones()
    {
        Assert.StartsWith("created new/dir/file.txt · 6 bytes · 1 lines", await _tools.Write("new/dir/file.txt", "hello\n"));
        Assert.StartsWith("Error: read src/b.cs before", await _tools.Write("src/b.cs", "replaced"));
        await _tools.Read("src/b.cs");
        Assert.StartsWith("overwrote src/b.cs", await _tools.Write("src/b.cs", "replaced"));
    }

    [Fact]
    public void List_honours_gitignore_and_depth()
    {
        string tree = _tools.List(".", 2);
        Assert.Contains("src/", tree);
        Assert.Contains("  a.cs", tree);
        Assert.DoesNotContain("bin/", tree);
        Assert.DoesNotContain("debug.log", tree);
        Assert.DoesNotContain(".git/", tree);
    }

    [Fact]
    public void Glob_matches_at_any_depth_and_skips_ignored_files()
    {
        string result = _tools.Glob("*.cs");
        Assert.Contains("src/a.cs", result);
        Assert.Contains("2 files", result);
        Assert.Contains("no matches", _tools.Glob("**/*.dll"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Grep_supports_files_content_and_count(bool ripgrep)
    {
        string? saved = FileTools.RipgrepPath;
        if (!ripgrep) FileTools.RipgrepPath = null;
        else if (saved is null) return;   // ripgrep not installed here
        try
        {
            Assert.Contains("src/a.cs", await _tools.Grep("class A"));
            Assert.DoesNotContain("src/b.cs", await _tools.Grep("class A"));
            Assert.Contains("src/a.cs:3:", await _tools.Grep("int x", mode: "content"));
            Assert.Contains("src/a.cs:1", await _tools.Grep("class", mode: "count"));
            Assert.Contains("no matches", await _tools.Grep("nothing-here"));
        }
        finally { FileTools.RipgrepPath = saved; }
    }

    [Fact]
    public void Paths_outside_the_workspace_are_rejected_even_through_symlinks()
    {
        Workspace ws = new(_root);
        Assert.Throws<WorkspaceAccessException>(() => ws.Resolve("../etc/passwd"));
        Assert.Throws<WorkspaceAccessException>(() => ws.Resolve("/etc/passwd"));
        if (OperatingSystem.IsWindows()) return;
        string outside = Directory.CreateTempSubdirectory("harness-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
            File.CreateSymbolicLink(Path.Combine(_root, "escape"), outside);
            Assert.Throws<WorkspaceAccessException>(() => ws.Resolve("escape/secret.txt"));
            Assert.Throws<WorkspaceAccessException>(() => ws.Resolve("escape/not-yet-created.txt"));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Gitignore_handles_negation_anchoring_and_directories()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.tmp\n!keep.tmp\n/root-only.txt\nbuild/\n");
        Gitignore g = new(_root);
        Assert.True(g.IsIgnored(Path.Combine(_root, "x.tmp"), false));
        Assert.False(g.IsIgnored(Path.Combine(_root, "keep.tmp"), false));
        Assert.True(g.IsIgnored(Path.Combine(_root, "root-only.txt"), false));
        Assert.False(g.IsIgnored(Path.Combine(_root, "src", "root-only.txt"), false));
        Assert.True(g.IsIgnored(Path.Combine(_root, "src", "build"), true));
        Assert.True(g.IsIgnored(Path.Combine(_root, "src", "build", "a.txt"), false));
        Assert.False(g.IsIgnored(Path.Combine(_root, "build"), false));   // a file named build is not the build/ directory
    }
}
