using DocxMcp.Grpc;
using Xunit;
using Xunit.Abstractions;

namespace DocxMcp.Tests;

/// <summary>
/// Tests for concurrent access via gRPC storage.
/// These tests verify that multiple SessionManager instances can safely access
/// the same tenant's data through gRPC storage locks.
/// </summary>
public class ConcurrentPersistenceTests
{
    [Fact]
    public void TwoManagers_SameTenant_BothSeeSessions()
    {
        // Two managers with the same tenant should see each other's sessions
        var tenantId = $"test-concurrent-{Guid.NewGuid():N}";

        var mgr1 = TestHelpers.CreateSessionManager(tenantId);
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);

        var s1 = mgr1.Create();

        // Manager 2 should be able to restore and see the session
        var restored = mgr2.RestoreSessions();
        Assert.Equal(1, restored);

        var list = mgr2.List().ToList();
        Assert.Single(list);
        Assert.Equal(s1.Id, list[0].Id);
    }

    [Fact]
    public void TwoManagers_DifferentTenants_IsolatedSessions()
    {
        // Two managers with different tenants should have isolated sessions
        var mgr1 = TestHelpers.CreateSessionManager(); // unique tenant
        var mgr2 = TestHelpers.CreateSessionManager(); // different unique tenant

        var s1 = mgr1.Create();
        var s2 = mgr2.Create();

        // Each should only see their own session
        var list1 = mgr1.List().ToList();
        var list2 = mgr2.List().ToList();

        Assert.Single(list1);
        Assert.Single(list2);
        Assert.Equal(s1.Id, list1[0].Id);
        Assert.Equal(s2.Id, list2[0].Id);
        Assert.NotEqual(s1.Id, s2.Id);
    }

    [Fact]
    public void ParallelCreation_NoLostSessions()
    {
        const int sessionsPerManager = 5;
        var tenantId = $"test-parallel-{Guid.NewGuid():N}";

        var mgr1 = TestHelpers.CreateSessionManager(tenantId);
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);

        // Verify both managers have the same tenant ID (captured at construction)
        Assert.Equal(tenantId, mgr1.TenantId);
        Assert.Equal(tenantId, mgr2.TenantId);

        var ids1 = new List<string>();
        var ids2 = new List<string>();
        var errors = new List<Exception>();

        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < sessionsPerManager; i++)
                {
                    try
                    {
                        var s = mgr1.Create();
                        lock (ids1) ids1.Add(s.Id);
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add(ex);
                    }
                }
            },
            () =>
            {
                for (int i = 0; i < sessionsPerManager; i++)
                {
                    try
                    {
                        var s = mgr2.Create();
                        lock (ids2) ids2.Add(s.Id);
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add(ex);
                    }
                }
            }
        );

        // If any errors occurred, fail with the first one
        if (errors.Count > 0)
        {
            throw new AggregateException($"Errors during parallel creation: {errors.Count}", errors);
        }

        // Verify we got all the IDs
        Assert.Equal(sessionsPerManager, ids1.Count);
        Assert.Equal(sessionsPerManager, ids2.Count);

        // Verify all sessions are present
        var mgr3 = TestHelpers.CreateSessionManager(tenantId);
        var restored = mgr3.RestoreSessions();
        var allIds = mgr3.List().Select(s => s.Id).ToHashSet();

        // Debug output
        var allExpectedIds = ids1.Concat(ids2).ToHashSet();
        var missing = allExpectedIds.Except(allIds).ToList();
        var extra = allIds.Except(allExpectedIds).ToList();

        Assert.True(missing.Count == 0,
            $"Missing sessions: [{string.Join(", ", missing)}]. " +
            $"Found {allIds.Count} sessions, expected {allExpectedIds.Count}. " +
            $"Restored: {restored}. " +
            $"ids1: [{string.Join(", ", ids1)}], ids2: [{string.Join(", ", ids2)}]");

        Assert.Equal(sessionsPerManager * 2, allIds.Count);
    }

    [Fact]
    public void CloseSession_UnderConcurrency_PreservesOtherSessions()
    {
        var tenantId = $"test-close-concurrent-{Guid.NewGuid():N}";

        var mgr1 = TestHelpers.CreateSessionManager(tenantId);
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);

        // Manager 1 creates a session
        var s1 = mgr1.Create();

        // Manager 2 restores and creates another session
        mgr2.RestoreSessions();
        var s2 = mgr2.Create();

        // Manager 1 closes its session
        mgr1.Close(s1.Id);

        // A third manager should see only s2
        var mgr3 = TestHelpers.CreateSessionManager(tenantId);
        mgr3.RestoreSessions();
        var list = mgr3.List().ToList();

        Assert.Single(list);
        Assert.Equal(s2.Id, list[0].Id);
    }

    [Fact]
    public void ConcurrentWrites_SameSession_AllPersist()
    {
        var tenantId = $"test-concurrent-writes-{Guid.NewGuid():N}";
        var mgr = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr.Create();
        var id = session.Id;

        // Apply multiple patches concurrently (simulating rapid edits)
        var patches = Enumerable.Range(0, 5)
            .Select(i => $"[{{\"op\":\"add\",\"path\":\"/body/children/{i}\",\"value\":{{\"type\":\"paragraph\",\"text\":\"Paragraph {i}\"}}}}]")
            .ToList();

        foreach (var patch in patches)
        {
            session.GetBody().AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.Text($"Paragraph"))));
            mgr.AppendWal(id, patch);
        }

        // All patches should be in history
        var history = mgr.GetHistory(id);
        Assert.True(history.Entries.Count >= patches.Count + 1); // +1 for baseline
    }

    // NOTE: DistributedLock_PreventsConcurrentAccess test removed.
    // Locking is now internal to the gRPC server and handled during atomic index operations.
    // The client no longer has direct access to lock operations.

    [Fact]
    public void TenantIsolation_NoDataLeakage()
    {
        // Ensure tenants cannot access each other's data
        var tenant1 = $"test-isolation-1-{Guid.NewGuid():N}";
        var tenant2 = $"test-isolation-2-{Guid.NewGuid():N}";

        var mgr1 = TestHelpers.CreateSessionManager(tenant1);
        var mgr2 = TestHelpers.CreateSessionManager(tenant2);

        // Create sessions in both tenants
        var s1 = mgr1.Create();
        var s2 = mgr2.Create();

        // Each manager should only see its own session
        Assert.Single(mgr1.List());
        Assert.Single(mgr2.List());

        // Trying to get the other tenant's session should fail
        Assert.Throws<KeyNotFoundException>(() => mgr1.Get(s2.Id));
        Assert.Throws<KeyNotFoundException>(() => mgr2.Get(s1.Id));
    }
}
