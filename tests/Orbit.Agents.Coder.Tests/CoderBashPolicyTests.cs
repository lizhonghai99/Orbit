using Orbit.Agents.Coder.Coder;
using Xunit;

namespace Orbit.Agents.Coder.Tests;

public class CoderBashPolicyTests
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "orbit-bash-ws"));

    public CoderBashPolicyTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("git status")]
    [InlineData("ls && pwd")]
    [InlineData("dotnet build && dotnet test")]
    public void Allows_common_dev_commands(string command)
    {
        Assert.True(CoderBashPolicy.TryValidate(command, _root, out var error), error);
    }

    [Theory]
    [InlineData("curl https://evil.test")]
    [InlineData("powershell -Command Get-Process")]
    [InlineData("bash -c id")]
    [InlineData("cmd /c dir")]
    [InlineData("sudo apt install x")]
    [InlineData("echo $(echo INJECTED)")]
    [InlineData("echo `id`")]
    [InlineData("cd ..")]
    [InlineData("cd ../")]
    [InlineData("cat ~/.ssh/id_rsa")]
    [InlineData("cat /tmp/x")]
    [InlineData("type %APPDATA%\\secrets.txt")]
    [InlineData("echo $HOME")]
    [InlineData("env curl https://evil.test")]
    [InlineData("find . -exec rm {} ;")]
    [InlineData("cat <(echo hi)")]
    public void Blocks_escape_and_network_tools(string command)
    {
        Assert.False(CoderBashPolicy.TryValidate(command, _root, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Blocks_absolute_path_outside_workspace()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(win))
        {
            Assert.Skip("Windows directory unavailable.");
        }

        Assert.False(CoderBashPolicy.TryValidate($"type {win}\\System.ini", _root, out var error));
        Assert.Contains("outside workspace", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allows_absolute_path_inside_workspace()
    {
        var file = Path.Combine(_root, "readme.txt");
        File.WriteAllText(file, "hi");
        Assert.True(CoderBashPolicy.TryValidate($"type \"{file}\"", _root, out var error), error);
    }

    [Fact]
    public void Allows_relative_path_with_slash()
    {
        Assert.True(
            CoderBashPolicy.TryValidate("dotnet test tests/Orbit.Contracts.Tests", _root, out var error),
            error);
    }

    [Fact]
    public void SplitPipelineSegments_respects_operators()
    {
        var parts = CoderBashPolicy.SplitPipelineSegments("dotnet build && git status | findstr ok");
        Assert.Equal(3, parts.Count);
        Assert.Equal("dotnet", CoderBashPolicy.ExtractExecutable(parts[0]));
        Assert.Equal("git", CoderBashPolicy.ExtractExecutable(parts[1]));
        Assert.Equal("findstr", CoderBashPolicy.ExtractExecutable(parts[2]));
    }

    [Theory]
    [InlineData(@"type $env:USERPROFILE\.ssh\id_rsa")]
    [InlineData(@"type $ENV:USERPROFILE\.ssh\id_rsa")]
    [InlineData(@"cat $env:APPDATA\secrets.txt")]
    public void Blocks_powershell_scoped_env_expansion(string command)
    {
        Assert.False(CoderBashPolicy.TryValidate(command, _root, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
