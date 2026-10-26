using System.Text;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

public class JsonlPartialLineRepairTests : IDisposable
{
    private readonly string _file;

    public JsonlPartialLineRepairTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "orbit-jsonl-repair-" + Guid.NewGuid().ToString("N") + ".jsonl");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_file))
            {
                File.Delete(_file);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void Repair_truncates_torn_tail_keeps_complete_lines()
    {
        var good = "{\"record\":\"entry\",\"data\":{\"id\":\"a\"}}\n";
        var torn = "{\"record\":\"entry\",\"data\":{\"id\":\"b\""; // no trailing newline
        File.WriteAllBytes(_file, Encoding.UTF8.GetBytes(good + torn));

        JsonlSessionStore.RepairTrailingPartialLine(_file);

        var text = File.ReadAllText(_file, Encoding.UTF8);
        Assert.Equal(good, text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Repair_noop_when_file_ends_with_newline()
    {
        var content = "{\"a\":1}\n{\"b\":2}\n";
        File.WriteAllText(_file, content, Encoding.UTF8);
        JsonlSessionStore.RepairTrailingPartialLine(_file);
        Assert.Equal(content, File.ReadAllText(_file, Encoding.UTF8));
    }
}
