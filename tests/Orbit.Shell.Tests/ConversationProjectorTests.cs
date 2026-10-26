using System.Text.Json;
using Orbit.Contracts;
using Orbit.Shell.I18n;
using Orbit.Shell.ViewModels;
using Xunit;

namespace Orbit.Shell.Tests;

public class ConversationProjectorTests
{
    public ConversationProjectorTests()
    {
        Loc.Set(UiLocale.En, persist: false);
    }

    [Fact]
    public void Project_drops_noise_meta_keeps_run_interrupted()
    {
        var entries = new List<SessionEntry>
        {
            Entry("m1", "meta", new { kind = "agui_run_started" }),
            Entry("m2", "meta", new { kind = "run_interrupted", reason = "stop" }),
            Entry("u1", "user", new { text = "hi" }),
            Entry("m3", "meta", new { kind = "agui_run_finished" }),
        };

        var rows = ConversationProjector.Project(entries);
        Assert.DoesNotContain(rows, r => r.EntryId is "m1" or "m3");
        Assert.Contains(rows, r => r.EntryId == "m2");
        Assert.Contains(rows, r => r.EntryId == "u1");
    }

    [Fact]
    public void Project_renders_research_stage_as_chip()
    {
        var entries = new List<SessionEntry>
        {
            Entry("r1", "meta", new { kind = SessionMetaKinds.ResearchStage, stage = "Outline" }),
        };

        var rows = ConversationProjector.Project(entries);
        var chip = Assert.Single(rows);
        Assert.Equal("r1", chip.EntryId);
        Assert.Equal(Loc.T("chat.research_stage", "Outline"), chip.DisplayBody);
        Assert.DoesNotContain('{', chip.DisplayBody);
    }

    [Fact]
    public void Project_hides_queued_approvals()
    {
        var entries = new List<SessionEntry>
        {
            Entry("a1", "approval", new { status = "queued", text = "wait" }),
            Entry("a2", "approval", new { status = "pending", text = "approve me" }),
        };

        var rows = ConversationProjector.Project(entries);
        Assert.DoesNotContain(rows, r => r.EntryId == "a1");
        Assert.Contains(rows, r => r.EntryId == "a2");
    }

    [Fact]
    public void Project_batches_adjacent_thinking()
    {
        var entries = new List<SessionEntry>
        {
            Entry("t1", "thinking", new { text = "one" }),
            Entry("t2", "thinking", new { text = "two" }),
            Entry("a1", "assistant", new { text = "done" }),
        };

        var rows = ConversationProjector.Project(entries);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r =>
            string.Equals(r.Type, "thinking", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, r => r.EntryId == "a1");
    }

    private static SessionEntry Entry(string id, string type, object payload) =>
        new()
        {
            Id = id,
            Type = type,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload)
        };
}
