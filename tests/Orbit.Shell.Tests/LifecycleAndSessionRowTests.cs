using System.Globalization;
using Orbit.Contracts;
using Orbit.Shell.I18n;
using Orbit.Shell.ViewModels;
using Xunit;

namespace Orbit.Shell.Tests;

public class LifecycleAndSessionRowTests
{
    [Fact]
    public void LifecycleLabelConverter_maps_enum_via_Loc()
    {
        Loc.Initialize();
        var text = LifecycleLabelConverter.Instance.Convert(
            AgentLifecycle.Running,
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal(Loc.T("agent.lifecycle.Running"), text);
    }

    [Fact]
    public void SessionRowVm_exposes_summary_and_archived_badge()
    {
        Loc.Initialize();
        var summary = new SessionSummary
        {
            SessionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            AgentId = "mail",
            Title = "Inbox triage",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            EntryCount = 3,
            IsArchived = true
        };
        var row = new SessionRowVm(summary);
        Assert.Equal(summary.SessionId, row.SessionId);
        Assert.Equal("Inbox triage", row.Title);
        Assert.True(row.IsArchived);
        Assert.Equal(Loc.T("app.archived_badge"), row.ArchivedBadge);
    }
}
