using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class OrbitApiErrorTests
{
    [Fact]
    public void TryParse_structured_code_and_details()
    {
        var json =
            """{"code":"confirm_required","message":"2 active","details":{"activeRuns":"2","agents":"coder"}}""";
        var err = OrbitApiError.TryParse(json);
        Assert.NotNull(err);
        Assert.Equal(OrbitErrorCodes.ConfirmRequired, err!.Code);
        Assert.Equal("2 active", err.Message);
        Assert.Equal("2", err.Details!["activeRuns"]);
        Assert.Equal("coder", err.Details["agents"]);
    }

    [Fact]
    public void TryParse_legacy_error_infers_code()
    {
        var err = OrbitApiError.TryParse("""{"error":"session_busy: wait"}""");
        Assert.NotNull(err);
        Assert.Equal(OrbitErrorCodes.SessionBusy, err!.Code);
        Assert.StartsWith("session_busy", err.Message);
    }

    [Fact]
    public void ToException_returns_OrbitApiException()
    {
        var ex = OrbitApiError.ToException("""{"code":"workspace_mismatch","message":"paths differ"}""");
        var api = Assert.IsType<OrbitApiException>(ex);
        Assert.True(api.Is(OrbitErrorCodes.WorkspaceMismatch));
        Assert.Equal("paths differ", api.Message);
    }

    [Fact]
    public void Shell_and_agent_tokens_are_distinct_holders()
    {
        var auth = new OrbitRuntimeAuth
        {
            ShellToken = OrbitLocalAuth.CreateToken(),
            AgentToken = OrbitLocalAuth.CreateToken()
        };
        Assert.False(string.Equals(auth.ShellToken, auth.AgentToken, StringComparison.Ordinal));
        Assert.False(OrbitLocalAuthGate.IsAuthorized(auth.AgentToken, auth.ShellToken!));
        Assert.True(OrbitLocalAuthGate.IsAuthorized(auth.ShellToken, auth.ShellToken!));
    }
}
