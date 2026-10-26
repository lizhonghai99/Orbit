using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class McpConfigRulesTests
{
    [Theory]
    [InlineData("tavily")]
    [InlineData("a")]
    [InlineData("my-server-1")]
    public void IsValidServerId_accepts_lowercase_digits_hyphen(string id) =>
        Assert.True(McpConfigRules.IsValidServerId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Tavily")]
    [InlineData("bad_id")]
    [InlineData("has space")]
    public void IsValidServerId_rejects_invalid(string? id) =>
        Assert.False(McpConfigRules.IsValidServerId(id));

    [Fact]
    public void Validate_stdio_requires_command()
    {
        var ex = Assert.Throws<ArgumentException>(() => McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig { Id = "tavily", Transport = "stdio", Trusted = false }
            ]
        }));
        Assert.Contains("command", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_http_requires_https()
    {
        var ex = Assert.Throws<ArgumentException>(() => McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig
                {
                    Id = "tavily",
                    Transport = "http",
                    Url = "http://example.com/mcp",
                    Trusted = false
                }
            ]
        }));
        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_duplicate_ids()
    {
        var ex = Assert.Throws<ArgumentException>(() => McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig { Id = "tavily", Transport = "stdio", Command = "npx" },
                new McpServerConfig { Id = "tavily", Transport = "stdio", Command = "npx" }
            ]
        }));
        Assert.Contains("Duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_unknown_transport()
    {
        Assert.Throws<ArgumentException>(() => McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig { Id = "x", Transport = "websocket", Command = "npx" }
            ]
        }));
    }

    [Fact]
    public void Validate_accepts_stdio_and_http()
    {
        var (normalized, warnings) = McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig
                {
                    Id = "local",
                    Transport = "stdio",
                    Command = "npx",
                    Args = ["-y", "tavily-mcp"],
                    Env = new Dictionary<string, string> { ["TAVILY_API_KEY"] = "${TAVILY_API_KEY}" },
                    Trusted = false,
                    Enabled = true
                },
                new McpServerConfig
                {
                    Id = "remote",
                    Transport = "http",
                    Url = "https://mcp.tavily.com/mcp",
                    Headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer ${TAVILY_API_KEY}"
                    },
                    Trusted = true
                }
            ]
        });

        Assert.Empty(warnings);
        Assert.Equal(2, normalized.Servers!.Count);
        Assert.Equal("stdio", normalized.Servers[0].Transport);
        Assert.Equal("http", normalized.Servers[1].Transport);
        Assert.False(normalized.Servers[0].Trusted);
        Assert.True(normalized.Servers[1].Trusted);
    }

    [Fact]
    public void Validate_warns_on_long_plaintext_header_but_does_not_block()
    {
        var secret = new string('x', 41);
        var (normalized, warnings) = McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig
                {
                    Id = "tavily",
                    Transport = "http",
                    Url = "https://mcp.tavily.com/mcp",
                    Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {secret}" }
                }
            ]
        });

        Assert.Single(normalized.Servers!);
        Assert.Contains(warnings, w => w.Contains("plaintext", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_allows_placeholder_header_without_warning()
    {
        var (_, warnings) = McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig
                {
                    Id = "tavily",
                    Transport = "http",
                    Url = "https://mcp.tavily.com/mcp/",
                    Headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer ${TAVILY_API_KEY}"
                    }
                }
            ]
        });

        Assert.Empty(warnings);
    }

    [Fact]
    public void Validate_clamps_timeout_and_defaults_when_zero()
    {
        var (normalized, _) = McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig
                {
                    Id = "a",
                    Transport = "stdio",
                    Command = "echo",
                    TimeoutSeconds = 0
                },
                new McpServerConfig
                {
                    Id = "b",
                    Transport = "stdio",
                    Command = "echo",
                    TimeoutSeconds = 9999
                }
            ]
        });

        Assert.Equal(30, normalized.Servers![0].TimeoutSeconds);
        Assert.Equal(300, normalized.Servers[1].TimeoutSeconds);
    }

    [Fact]
    public void Validate_trims_id_and_defaults_transport_to_stdio()
    {
        var (normalized, _) = McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig { Id = "  tavily  ", Command = "npx" }
            ]
        });

        Assert.Equal("tavily", normalized.Servers![0].Id);
        Assert.Equal("stdio", normalized.Servers[0].Transport);
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("Bearer ${TAVILY_API_KEY}", false)]
    public void LooksLikePlaintextSecret_cases(string value, bool expected) =>
        Assert.Equal(expected, McpConfigRules.LooksLikePlaintextSecret(value));

    [Fact]
    public void LooksLikePlaintextSecret_true_when_long_without_placeholder() =>
        Assert.True(McpConfigRules.LooksLikePlaintextSecret(new string('a', 41)));

    [Fact]
    public void NormalizeToolPrefix_rejects_invalid()
    {
        Assert.Throws<ArgumentException>(() =>
            McpConfigRules.NormalizeToolPrefix("bad prefix!", "tavily"));
        Assert.Throws<ArgumentException>(() =>
            McpConfigRules.NormalizeToolPrefix(new string('a', 33), "tavily"));
    }

    [Fact]
    public void NormalizeToolPrefix_allows_empty_and_valid()
    {
        Assert.Equal("", McpConfigRules.NormalizeToolPrefix("", "tavily"));
        Assert.Null(McpConfigRules.NormalizeToolPrefix(null, "tavily"));
        Assert.Equal("tv_", McpConfigRules.NormalizeToolPrefix("tv_", "tavily"));
    }

    [Fact]
    public void Validate_null_file_or_servers_throws()
    {
        var missingBody = Assert.Throws<ArgumentException>(() =>
            McpConfigRules.ValidateAndNormalize(null));
        Assert.Contains("mcp_body_required", missingBody.Message, StringComparison.Ordinal);

        var missingServers = Assert.Throws<ArgumentException>(() =>
            McpConfigRules.ValidateAndNormalize(new McpConfigFile()));
        Assert.Contains("mcp_servers_required", missingServers.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_empty_servers_warns_and_null_args_ok()
    {
        var (normalized, warnings) = McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers = []
        });
        Assert.Empty(normalized.Servers!);
        Assert.Contains(warnings, w => w.Contains("empty", StringComparison.OrdinalIgnoreCase));

        var (withNullArgs, _) = McpConfigRules.ValidateAndNormalize(new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig
                {
                    Id = "tavily",
                    Transport = "stdio",
                    Command = "npx",
                    Args = null!,
                    Trusted = false
                }
            ]
        });
        Assert.Empty(withNullArgs.Servers![0].Args);
    }
}

