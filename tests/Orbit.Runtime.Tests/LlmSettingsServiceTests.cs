using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Llm;
using Xunit;

namespace Orbit.Runtime.Tests;

[Collection("orbit-env")]
public class LlmSettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public LlmSettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "orbit-llm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "llm.settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void SaveProfile_persists_dpapi_key_not_plaintext()
    {
        var sut = new LlmSettingsService(_path);
        var status = sut.SaveProfile(new SaveLlmProfileRequest
        {
            ProfileId = "custom",
            Endpoint = "https://example.com/v1",
            ApiKey = "sk-test-secret",
            Protocol = LlmProtocol.Responses,
            Model = "demo-model",
            SetActive = true
        });

        Assert.Equal("responses", status.Mode);
        Assert.Equal("custom", status.ActiveProfileId);
        Assert.Equal("demo-model", status.Model);
        Assert.True(status.HasApiKey);
        Assert.DoesNotContain("sk-test-secret", JsonSerializer.Serialize(status), StringComparison.Ordinal);

        var disk = File.ReadAllText(_path);
        Assert.DoesNotContain("sk-test-secret", disk, StringComparison.Ordinal);
        Assert.Contains("dpapi:v1:", disk, StringComparison.Ordinal);

        var env = sut.SnapshotEnvForAgents();
        Assert.Equal("sk-test-secret", env["ORBIT_LLM_API_KEY"]);
        Assert.Equal("demo-model", env[LlmProfileCatalog.ActiveModelKey]);
        Assert.Equal("custom", env[LlmProfileCatalog.ActiveProfileKey]);
    }

    [Fact]
    public void Ctor_does_not_migrate_env_key_onto_disk()
    {
        var prev = Environment.GetEnvironmentVariable("ORBIT_LLM_API_KEY", EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_LLM_API_KEY", "sk-env-should-not-land", EnvironmentVariableTarget.Process);
            _ = new LlmSettingsService(_path);
            Assert.False(File.Exists(_path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_LLM_API_KEY", prev, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public async Task ListModels_rejects_caller_endpoint_with_stored_key_only()
    {
        var sut = new LlmSettingsService(_path);
        sut.SaveProfile(new SaveLlmProfileRequest
        {
            ProfileId = "custom",
            Endpoint = "https://api.example.com/v1",
            ApiKey = "sk-stored",
            Protocol = LlmProtocol.Responses,
            Model = "m",
            SetActive = true
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ListModelsAsync(new ListLlmModelsRequest
            {
                ProfileId = "custom",
                Endpoint = "https://evil.example/v1",
                ApiKey = null
            }, TestContext.Current.CancellationToken));

        Assert.Contains("explicit API Key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetReasoning_updates_status()
    {
        var sut = new LlmSettingsService(_path);
        sut.SaveProfile(new SaveLlmProfileRequest
        {
            ProfileId = "custom",
            Endpoint = "https://example.com/v1",
            ApiKey = "sk-x",
            Protocol = LlmProtocol.Responses,
            Model = "m",
            SetActive = true
        });

        var status = sut.SetReasoning(false, "high");
        Assert.False(status.ReasoningEnabled);

        status = sut.SetReasoning(true, "high");
        Assert.True(status.ReasoningEnabled);
        Assert.Equal("high", status.ReasoningEffort);
        Assert.Equal("1", sut.SnapshotEnvForAgents()[LlmProfileCatalog.ReasoningEnabledKey]);
        Assert.Equal("high", sut.SnapshotEnvForAgents()[LlmProfileCatalog.ReasoningEffortKey]);
    }
}
