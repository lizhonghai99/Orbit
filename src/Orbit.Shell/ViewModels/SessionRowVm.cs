using Orbit.Contracts;
using Orbit.Shell.I18n;

namespace Orbit.Shell.ViewModels;

/// <summary>Session list row with localized chrome (archived badge).</summary>
public sealed class SessionRowVm(SessionSummary summary)
{
    public SessionSummary Summary { get; } = summary;
    public string SessionId => Summary.SessionId;
    public string Title => SessionTitleDisplay.Display(Summary.Title);
    public DateTimeOffset UpdatedAt => Summary.UpdatedAt;
    public string? WorkspacePath => Summary.WorkspacePath;
    public bool IsArchived => Summary.IsArchived;
    public int EntryCount => Summary.EntryCount;
    public string ArchivedBadge => Loc.T("app.archived_badge");
}
