using Orbit.Contracts;
using Orbit.Shell.Controllers;
using Orbit.Shell.I18n;
using Xunit;

namespace Orbit.Shell.Tests;

public class SessionErrorLocalizationTests
{
    public SessionErrorLocalizationTests()
    {
        Loc.Initialize();
    }

    [Fact]
    public void LocalizeCreateSessionError_uses_OrbitApiException_code()
    {
        var ex = new OrbitApiException(
            OrbitErrorCodes.WorkspaceRequired,
            "workspace_required: pick a folder");
        Assert.Equal(Loc.T("ws.required"), SessionListController.LocalizeCreateSessionError(ex));
    }

    [Fact]
    public void LocalizeCreateSessionError_legacy_message_prefix()
    {
        Assert.Equal(
            Loc.T("ws.missing"),
            SessionListController.LocalizeCreateSessionError("workspace_missing: C:\\gone"));
    }

    [Fact]
    public void LocalizeCreateSessionError_passes_through_unknown()
    {
        const string raw = "something else failed";
        Assert.Equal(raw, SessionListController.LocalizeCreateSessionError(raw));
    }
}
