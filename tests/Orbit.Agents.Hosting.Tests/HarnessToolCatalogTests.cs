using Microsoft.Agents.AI;
using Orbit.Agents.Hosting;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class HarnessToolCatalogTests
{
    [Fact]
    public void FileAccess_and_FileMemory_match_provider_constants()
    {
#pragma warning disable MAAI001
        var stubs = HarnessToolCatalog.ForEnabled(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OrbitHarnessIds.FileAccess,
            OrbitHarnessIds.FileMemory
        });
        var names = stubs.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(FileAccessProvider.LsToolName, names);
        Assert.Contains(FileAccessProvider.ReadFileToolName, names);
        Assert.Contains(FileAccessProvider.ReadLinesToolName, names);
        Assert.Contains(FileAccessProvider.GrepToolName, names);
        Assert.Contains(FileAccessProvider.WriteToolName, names);
        Assert.Contains(FileAccessProvider.ReplaceToolName, names);
        Assert.Contains(FileAccessProvider.ReplaceLinesToolName, names);
        Assert.Contains(FileAccessProvider.DeleteFileToolName, names);

        Assert.Contains(FileMemoryProvider.LsToolName, names);
        Assert.Contains(FileMemoryProvider.ReadFileToolName, names);
        Assert.Contains(FileMemoryProvider.GrepToolName, names);
        Assert.Contains(FileMemoryProvider.WriteToolName, names);
        Assert.Contains(FileMemoryProvider.ReplaceToolName, names);
        Assert.Contains(FileMemoryProvider.ReplaceLinesToolName, names);
        Assert.Contains(FileMemoryProvider.DeleteFileToolName, names);
        Assert.DoesNotContain("file_memory_list", names);
#pragma warning restore MAAI001
    }

    [Fact]
    public void Todo_Mode_Background_golden_names_have_no_fiction()
    {
        var todo = HarnessToolCatalog.ForEnabled(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OrbitHarnessIds.Todo
        }).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            HarnessToolCatalog.TodoToolNames.ToHashSet(StringComparer.Ordinal),
            todo);
        Assert.DoesNotContain("todos_list", todo);
        Assert.DoesNotContain("todos_update", todo);

        var mode = HarnessToolCatalog.ForEnabled(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OrbitHarnessIds.Mode
        }).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            HarnessToolCatalog.ModeToolNames.ToHashSet(StringComparer.Ordinal),
            mode);

        var bg = HarnessToolCatalog.ForEnabled(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OrbitHarnessIds.Background },
            backgroundAgentsMounted: true).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            HarnessToolCatalog.BackgroundToolNames.ToHashSet(StringComparer.Ordinal),
            bg);
        Assert.Contains("background_agents_get_all_tasks", bg);
    }

    [Fact]
    public void Skills_include_load_read_and_run()
    {
#pragma warning disable MAAI001
        var names = HarnessToolCatalog.ForEnabled(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OrbitHarnessIds.Skills
        }).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(AgentSkillsProvider.LoadSkillToolName, names);
        Assert.Contains(AgentSkillsProvider.ReadSkillResourceToolName, names);
        Assert.Contains(AgentSkillsProvider.RunSkillScriptToolName, names);
#pragma warning restore MAAI001
    }
}
