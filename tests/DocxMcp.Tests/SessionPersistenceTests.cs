using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Persistence;
using DocxMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocxMcp.Tests;

public class SessionPersistenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionStore _store;

    public SessionPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "docx-mcp-tests", Guid.NewGuid().ToString("N"));
        _store = new SessionStore(NullLogger<SessionStore>.Instance, _tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private SessionManager CreateManager() =>
        new SessionManager(_store, NullLogger<SessionManager>.Instance);

    [Fact]
    public void OpenSession_PersistsBaselineAndIndex()
    {
        var mgr = CreateManager();
        var session = mgr.Create();

        Assert.True(File.Exists(_store.BaselinePath(session.Id)));
        Assert.True(File.Exists(Path.Combine(_tempDir, "index.json")));

        var index = _store.LoadIndex();
        Assert.Single(index.Sessions);
        Assert.Equal(session.Id, index.Sessions[0].Id);
    }

    [Fact]
    public void CloseSession_RemovesFromDisk()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        mgr.Close(id);

        Assert.False(File.Exists(_store.BaselinePath(id)));
        var index = _store.LoadIndex();
        Assert.Empty(index.Sessions);
    }

    [Fact]
    public void AppendWal_WritesToMappedFile()
    {
        var mgr = CreateManager();
        var session = mgr.Create();

        mgr.AppendWal(session.Id, "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"Hello\"}}]");

        var walEntries = _store.ReadWal(session.Id);
        Assert.Single(walEntries);

        var index = _store.LoadIndex();
        Assert.Equal(1, index.Sessions[0].WalCount);
    }

    [Fact]
    public void Compact_ResetsWalAndUpdatesBaseline()
    {
        var mgr = CreateManager();
        var session = mgr.Create();

        // Add content via patch
        var body = session.GetBody();
        body.AppendChild(new Paragraph(new Run(new Text("Test content"))));

        mgr.AppendWal(session.Id, "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"Test\"}}]");

        mgr.Compact(session.Id);

        var index = _store.LoadIndex();
        Assert.Equal(0, index.Sessions[0].WalCount);

        // WAL should be empty after compaction
        var walEntries = _store.ReadWal(session.Id);
        Assert.Empty(walEntries);
    }

    [Fact]
    public void AppendWal_AutoCompacts_WhenThresholdReached()
    {
        // Use a custom store with a low compaction threshold
        var customTempDir = Path.Combine(Path.GetTempPath(), "docx-mcp-tests", Guid.NewGuid().ToString("N"));
        var customStore = new SessionStore(NullLogger<SessionStore>.Instance, customTempDir);

        try
        {
            // Set threshold to 5 via environment variable before creating the manager
            var originalThreshold = Environment.GetEnvironmentVariable("DOCX_MCP_WAL_COMPACT_THRESHOLD");
            Environment.SetEnvironmentVariable("DOCX_MCP_WAL_COMPACT_THRESHOLD", "5");

            try
            {
                var mgr = new SessionManager(customStore, NullLogger<SessionManager>.Instance);
                var session = mgr.Create();
                var id = session.Id;

                // Append 4 WAL entries (threshold is 5, so no compaction yet)
                for (int i = 0; i < 4; i++)
                {
                    session.GetBody().AppendChild(new Paragraph(new Run(new Text($"Entry {i}"))));
                    mgr.AppendWal(id, $"[{{\"op\":\"add\",\"path\":\"/body/children/{i}\",\"value\":{{\"type\":\"paragraph\",\"text\":\"Entry {i}\"}}}}]");
                }

                var indexBefore = customStore.LoadIndex();
                Assert.Equal(4, indexBefore.Sessions[0].WalCount);

                // Append the 5th entry — should trigger auto-compaction
                session.GetBody().AppendChild(new Paragraph(new Run(new Text("Entry 4"))));
                mgr.AppendWal(id, "[{\"op\":\"add\",\"path\":\"/body/children/4\",\"value\":{\"type\":\"paragraph\",\"text\":\"Entry 4\"}}]");

                var indexAfter = customStore.LoadIndex();
                Assert.Equal(0, indexAfter.Sessions[0].WalCount); // Compaction reset WAL count

                // WAL should be empty after compaction
                var walEntries = customStore.ReadWal(id);
                Assert.Empty(walEntries);
            }
            finally
            {
                // Restore original environment variable
                Environment.SetEnvironmentVariable("DOCX_MCP_WAL_COMPACT_THRESHOLD", originalThreshold);
            }
        }
        finally
        {
            customStore.Dispose();
            if (Directory.Exists(customTempDir))
                Directory.Delete(customTempDir, recursive: true);
        }
    }

    [Fact]
    public void RestoreSessions_RehydratesFromBaseline()
    {
        // Create a session and persist it
        var mgr1 = CreateManager();
        var session = mgr1.Create();
        var id = session.Id;

        // Add content directly to DOM
        var body = session.GetBody();
        body.AppendChild(new Paragraph(new Run(new Text("Persisted content"))));

        // Compact to save current state as baseline
        mgr1.Compact(id);

        // Simulate server restart: create a new manager with the same store
        _store.Dispose(); // close existing WAL mappings
        var store2 = new SessionStore(NullLogger<SessionStore>.Instance, _tempDir);
        var mgr2 = new SessionManager(store2, NullLogger<SessionManager>.Instance);

        var restored = mgr2.RestoreSessions();
        Assert.Equal(1, restored);

        // Verify the session is accessible with the same ID
        var restoredSession = mgr2.Get(id);
        Assert.NotNull(restoredSession);
        Assert.Contains("Persisted content", restoredSession.GetBody().InnerText);

        store2.Dispose();
    }

    [Fact]
    public void RestoreSessions_ReplaysWal()
    {
        // Create a session and add a patch via WAL (not compacted)
        var mgr1 = CreateManager();
        var session = mgr1.Create();
        var id = session.Id;

        // Apply a patch through PatchTool
        PatchTool.ApplyPatch(mgr1, null, id,
            "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"WAL entry\"}}]");

        // Verify WAL has entries
        var walEntries = _store.ReadWal(id);
        Assert.NotEmpty(walEntries);

        // Simulate restart
        _store.Dispose();
        var store2 = new SessionStore(NullLogger<SessionStore>.Instance, _tempDir);
        var mgr2 = new SessionManager(store2, NullLogger<SessionManager>.Instance);

        var restored = mgr2.RestoreSessions();
        Assert.Equal(1, restored);

        // Verify the WAL was replayed — the paragraph should exist
        var restoredSession = mgr2.Get(id);
        Assert.Contains("WAL entry", restoredSession.GetBody().InnerText);

        store2.Dispose();
    }

    [Fact]
    public void RestoreSessions_CorruptBaseline_SkipsButPreservesIndex()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        // Corrupt the baseline file
        File.WriteAllBytes(_store.BaselinePath(id), new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

        // Simulate restart
        _store.Dispose();
        var store2 = new SessionStore(NullLogger<SessionStore>.Instance, _tempDir);
        var mgr2 = new SessionManager(store2, NullLogger<SessionManager>.Instance);

        var restored = mgr2.RestoreSessions();
        Assert.Equal(0, restored); // Session not restored to memory

        // Index entry should be preserved (WAL history preservation)
        var index = store2.LoadIndex();
        Assert.Single(index.Sessions);
        Assert.Equal(id, index.Sessions[0].Id);

        store2.Dispose();
    }

    [Fact]
    public void MultipleSessions_PersistIndependently()
    {
        var mgr = CreateManager();
        var s1 = mgr.Create();
        var s2 = mgr.Create();

        Assert.True(File.Exists(_store.BaselinePath(s1.Id)));
        Assert.True(File.Exists(_store.BaselinePath(s2.Id)));

        var index = _store.LoadIndex();
        Assert.Equal(2, index.Sessions.Count);

        mgr.Close(s1.Id);

        index = _store.LoadIndex();
        Assert.Single(index.Sessions);
        Assert.Equal(s2.Id, index.Sessions[0].Id);
    }

    [Fact]
    public void DocumentSnapshot_CompactsViaToolCall()
    {
        var mgr = CreateManager();
        var session = mgr.Create();

        mgr.AppendWal(session.Id, "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"Before snapshot\"}}]");

        var result = DocumentTools.DocumentSnapshot(mgr, session.Id);
        Assert.Contains("Snapshot created", result);

        var index = _store.LoadIndex();
        Assert.Equal(0, index.Sessions[0].WalCount);
    }
}
