using DocxMcp.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocxMcp.Tests;

public class ConcurrentPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public ConcurrentPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "docx-mcp-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private SessionStore CreateStore() =>
        new SessionStore(NullLogger<SessionStore>.Instance, _tempDir);

    private SessionManager CreateManager(SessionStore store) =>
        new SessionManager(store, NullLogger<SessionManager>.Instance);

    [Fact]
    public void AcquireLock_ReturnsDisposableLock()
    {
        using var store = CreateStore();
        store.EnsureDirectory();

        using var sessionLock = store.AcquireLock();
        // Lock acquired successfully; verify it's IDisposable and non-null
        Assert.NotNull(sessionLock);
    }

    [Fact]
    public void AcquireLock_ReleasedOnDispose()
    {
        using var store = CreateStore();
        store.EnsureDirectory();

        var lock1 = store.AcquireLock();
        lock1.Dispose();

        // Should succeed now that lock1 is released
        using var lock2 = store.AcquireLock(maxRetries: 1, initialDelayMs: 10);
        Assert.NotNull(lock2);
    }

    [Fact]
    public void AcquireLock_DoubleDispose_DoesNotThrow()
    {
        using var store = CreateStore();
        store.EnsureDirectory();

        var sessionLock = store.AcquireLock();
        sessionLock.Dispose();
        sessionLock.Dispose(); // Should not throw
    }

    [Fact]
    public void TwoManagers_BothCreateSessions_IndexContainsBoth()
    {
        // Simulates two processes sharing the same sessions directory.
        // Each manager creates a session; both should be in the index.
        using var store1 = CreateStore();
        using var store2 = CreateStore();

        var mgr1 = CreateManager(store1);
        var mgr2 = CreateManager(store2);

        var s1 = mgr1.Create();
        var s2 = mgr2.Create();

        // Reload index from disk to see the merged result
        var index = store1.LoadIndex();
        var ids = index.Sessions.Select(e => e.Id).ToHashSet();

        Assert.Contains(s1.Id, ids);
        Assert.Contains(s2.Id, ids);
        Assert.Equal(2, index.Sessions.Count);
    }

    [Fact]
    public void TwoManagers_ParallelCreation_NoLostSessions()
    {
        const int sessionsPerManager = 5;

        using var store1 = CreateStore();
        using var store2 = CreateStore();

        var mgr1 = CreateManager(store1);
        var mgr2 = CreateManager(store2);

        var ids1 = new List<string>();
        var ids2 = new List<string>();

        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < sessionsPerManager; i++)
                {
                    var s = mgr1.Create();
                    lock (ids1) ids1.Add(s.Id);
                }
            },
            () =>
            {
                for (int i = 0; i < sessionsPerManager; i++)
                {
                    var s = mgr2.Create();
                    lock (ids2) ids2.Add(s.Id);
                }
            }
        );

        // Verify all sessions present in the index
        var index = store1.LoadIndex();
        var indexIds = index.Sessions.Select(e => e.Id).ToHashSet();

        foreach (var id in ids1.Concat(ids2))
            Assert.Contains(id, indexIds);

        Assert.Equal(sessionsPerManager * 2, index.Sessions.Count);
    }

    [Fact]
    public void WithLockedIndex_ReloadsFromDisk()
    {
        // Verifies that WithLockedIndex always reloads from disk,
        // so external writes are not lost.
        using var store1 = CreateStore();
        using var store2 = CreateStore();

        var mgr1 = CreateManager(store1);
        var mgr2 = CreateManager(store2);

        // Manager 1 creates a session
        var s1 = mgr1.Create();

        // Manager 2 creates a session (its WithLockedIndex should reload and see s1)
        var s2 = mgr2.Create();

        // Now manager 1 creates another session — should still see s2
        var s3 = mgr1.Create();

        var index = store1.LoadIndex();
        Assert.Equal(3, index.Sessions.Count);

        var ids = index.Sessions.Select(e => e.Id).ToHashSet();
        Assert.Contains(s1.Id, ids);
        Assert.Contains(s2.Id, ids);
        Assert.Contains(s3.Id, ids);
    }

    [Fact]
    public void MappedWal_Refresh_SeesExternalAppend()
    {
        using var store = CreateStore();
        store.EnsureDirectory();

        // Open the WAL via the store (simulating process A)
        var walA = store.GetOrCreateWal("shared");
        walA.Append("{\"patches\":\"first\"}");
        Assert.Equal(1, walA.EntryCount);

        // Simulate process B writing directly to the same WAL file
        // by using a second MappedWal instance on the same path
        var walPath = store.WalPath("shared");
        using var walB = new MappedWal(walPath);
        walB.Append("{\"patches\":\"second\"}");

        // walA doesn't see it yet (stale in-memory offset)
        Assert.Equal(1, walA.EntryCount);

        // After Refresh(), walA should see both entries
        walA.Refresh();
        Assert.Equal(2, walA.EntryCount);

        var all = walA.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Contains("first", all[0]);
        Assert.Contains("second", all[1]);
    }

    [Fact]
    public void CloseSession_RemovesFromIndex()
    {
        using var store = CreateStore();
        var mgr = CreateManager(store);

        var s = mgr.Create();
        var id = s.Id;

        mgr.Close(id);

        var idx = store.LoadIndex();
        Assert.Empty(idx.Sessions);
    }

    [Fact]
    public void CloseSession_UnderConcurrency_PreservesOtherSessions()
    {
        using var store1 = CreateStore();
        using var store2 = CreateStore();

        var mgr1 = CreateManager(store1);
        var mgr2 = CreateManager(store2);

        // Both managers create sessions
        var s1 = mgr1.Create();
        var s2 = mgr2.Create();

        // Manager 1 closes its session
        mgr1.Close(s1.Id);

        // Index should still contain s2
        var index = store1.LoadIndex();
        Assert.Single(index.Sessions);
        Assert.Equal(s2.Id, index.Sessions[0].Id);
    }
}
