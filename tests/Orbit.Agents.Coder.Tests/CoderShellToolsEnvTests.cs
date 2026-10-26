using System.Diagnostics;
using Orbit.Agents.Coder.Coder;
using Xunit;

namespace Orbit.Agents.Coder.Tests;

public class CoderShellQuoteTests
{
    [Fact]
    public void QuoteBash_prefers_single_quotes_without_backslash_doubling()
    {
        var q = CoderShellTools.QuoteBash(@"cd D:\src && dotnet test");
        Assert.Equal(@"'cd D:\src && dotnet test'", q);
    }

    [Fact]
    public void QuoteBash_apostrophe_uses_posix_end_quote_escape()
    {
        var q = CoderShellTools.QuoteBash("echo 'hi'");
        Assert.Equal("'echo '\\''hi'\\'''", q);
    }

    [Fact]
    public void QuoteBash_dollar_paren_stays_inside_single_quotes()
    {
        var q = CoderShellTools.QuoteBash("echo '$(curl evil)'");
        Assert.StartsWith("'", q);
        Assert.EndsWith("'", q);
        Assert.DoesNotContain("\"", q);
        // Entire command is single-quoted (with '\'' islands); bash -lc will not expand $().
        Assert.Contains("$(curl evil)", q, StringComparison.Ordinal);
    }

    [Fact]
    public void IsSecretEnvName_detects_keys_and_tokens()
    {
        Assert.True(CoderShellTools.IsSecretEnvName("DEEPSEEK_API_KEY"));
        Assert.True(CoderShellTools.IsSecretEnvName("ORBIT_RUNTIME_TOKEN"));
        Assert.False(CoderShellTools.IsSecretEnvName("PATH"));
    }

    [Fact]
    public void ApplyEnvironmentWhitelist_strips_secrets()
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        // ProcessStartInfo starts with a copy of current env on some hosts; clear via whitelist.
        CoderShellTools.ApplyEnvironmentWhitelist(psi);
        Assert.DoesNotContain(psi.Environment.Keys, CoderShellTools.IsSecretEnvName);
        Assert.True(psi.Environment.ContainsKey("PATH") || psi.Environment.ContainsKey("Path"));
    }
}

/// <summary>
/// Bash engine resolution: Coder fails closed instead of switching shells, because CoderBashPolicy
/// validates bash syntax only.
/// </summary>
public class CoderShellBashResolutionTests
{
    [Fact]
    public void ResolveBashPath_honours_ORBIT_BASH_PATH_override()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"orbit-fake-bash-{Guid.NewGuid():N}.exe");
        File.WriteAllText(fake, "stub");
        var previous = Environment.GetEnvironmentVariable("ORBIT_BASH_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_PATH", fake);
            Assert.Equal(Path.GetFullPath(fake), CoderShellTools.ResolveBashPath());
            Assert.Equal("git-bash", CoderShellTools.DescribePreferredShell());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_PATH", previous);
            File.Delete(fake);
        }
    }

    [Fact]
    public void ResolveBashPath_fails_closed_when_override_is_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"orbit-no-bash-{Guid.NewGuid():N}.exe");
        var previous = Environment.GetEnvironmentVariable("ORBIT_BASH_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_PATH", missing);
            // Explicit bad override must not silently fall back to Git Bash or to another engine.
            Assert.Null(CoderShellTools.ResolveBashPath());
            Assert.Equal("none", CoderShellTools.DescribePreferredShell());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_PATH", previous);
        }
    }

    [Fact]
    public async Task RunBashAsync_reports_unavailable_when_bash_is_missing()
    {
        var previous = Environment.GetEnvironmentVariable("ORBIT_BASH_PATH");
        var missing = Path.Combine(Path.GetTempPath(), $"orbit-no-bash-{Guid.NewGuid():N}.exe");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_PATH", missing);
            var tools = new CoderShellTools(Path.GetTempPath());
            var output = await tools.RunBashAsync("pwd", TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Contains("bash unavailable", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_PATH", previous);
        }
    }
}
