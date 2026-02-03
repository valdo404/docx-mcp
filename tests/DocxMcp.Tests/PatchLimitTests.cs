using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

public class PatchLimitTests : IDisposable
{
    private readonly DocxSession _session;
    private readonly SessionManager _sessions;

    public PatchLimitTests()
    {
        _sessions = TestHelpers.CreateSessionManager();
        _session = _sessions.Create();

        var body = _session.GetBody();
        body.AppendChild(new Paragraph(new Run(new Text("Content"))));
    }

    [Fact]
    public void TenPatchesAreAccepted()
    {
        var patches = new List<object>();
        for (int i = 0; i < 10; i++)
        {
            patches.Add(new
            {
                op = "add",
                path = "/body/children/0",
                value = new { type = "paragraph", text = $"Added {i}" }
            });
        }

        var json = JsonSerializer.Serialize(patches);
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(10, doc.RootElement.GetProperty("applied").GetInt32());
    }

    [Fact]
    public void ElevenPatchesAreRejected()
    {
        var patches = new List<object>();
        for (int i = 0; i < 11; i++)
        {
            patches.Add(new
            {
                op = "add",
                path = "/body/children/0",
                value = new { type = "paragraph", text = $"Added {i}" }
            });
        }

        var json = JsonSerializer.Serialize(patches);
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Too many operations", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void OnePatchIsAccepted()
    {
        var json = """[{"op": "add", "path": "/body/children/0", "value": {"type": "paragraph", "text": "Hello"}}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("applied").GetInt32());
    }

    [Fact]
    public void EmptyPatchArrayIsAccepted()
    {
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, "[]");

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("applied").GetInt32());
    }

    public void Dispose()
    {
        _sessions.Close(_session.Id);
    }
}
