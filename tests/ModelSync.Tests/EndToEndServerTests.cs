using Microsoft.AspNetCore.Mvc.Testing;
using ModelSync.Core;
using ModelSync.Sdk;
using Xunit;

namespace ModelSync.Tests;

/// <summary>
/// The "Google Docs for models" scenario end to end: a real ModelSync server
/// (in-process ASP.NET Core host, real gRPC over HTTP/2) with two local
/// clients, Alice and Bob, connected to their private workspaces. They edit
/// concurrently, update and commit against the shared public workspace, and
/// must always converge.
/// </summary>
public class EndToEndServerTests
{
    private const string ServerAddress = "http://localhost";

    private static WebApplicationFactory<Program> NewServer() => new();

    private static async Task<(WorkspaceSession Alice, WorkspaceSession Bob)> ConnectAliceAndBobAsync(
        WebApplicationFactory<Program> server)
    {
        var alice = await WorkspaceSession.ConnectAsync(ServerAddress, "alice", server.Server.CreateHandler());
        var bob = await WorkspaceSession.ConnectAsync(ServerAddress, "bob", server.Server.CreateHandler());
        return (alice, bob);
    }

    [Fact]
    public async Task MetamodelAndModelSyncBetweenTwoClients()
    {
        await using var server = NewServer();
        var (alice, bob) = await ConnectAliceAndBobAsync(server);
        await using var _ = alice;
        await using var __ = bob;

        // Alice models a small metamodel — types are ordinary elements
        // (streamlined MOF), so the metamodel syncs through the same operations.
        var classType = await alice.CreateElementAsync(typeId: null);
        await alice.SetPropertyAsync(classType, "name", PropertyValue.String("Class"));
        await alice.PutMapEntryAsync(classType, "propertySchema", "name", PropertyValue.String("Single<String>"));
        await alice.PutMapEntryAsync(classType, "propertySchema", "methods", PropertyValue.String("List<Method>"));

        // ... and an instance of it.
        var monitor = await alice.CreateElementAsync(typeId: classType);
        await alice.SetPropertyAsync(monitor, "name", PropertyValue.String("HeartbeatMonitor"));
        await alice.AppendListItemAsync(monitor, "methods", PropertyValue.String("measureHeartbeat"));

        var commit = await alice.CommitAsync();
        Assert.True(commit.Success);

        // Bob pulls and sees metamodel + model, reconstructed purely from operations.
        var update = await bob.UpdateAsync();
        Assert.False(update.WasUpToDate);
        Assert.Empty(update.Conflicts);

        var bobClassType = bob.Model.GetElement(classType);
        Assert.NotNull(bobClassType);
        Assert.Equal("Class", bobClassType!.GetProperty("name")!.SingleValue!.Content);
        Assert.Equal("Single<String>", bobClassType.GetProperty("propertySchema")!.MapValues["name"].Content);

        var bobMonitor = bob.Model.GetElement(monitor);
        Assert.NotNull(bobMonitor);
        Assert.Equal(classType, bobMonitor!.TypeId);
        Assert.Equal(new[] { "measureHeartbeat" },
            bobMonitor.GetProperty("methods")!.ListItems.Select(i => i.Value.Content));

        ModelAssert.Equivalent(alice.Model, bob.Model);
    }

    [Fact]
    public async Task ConcurrentConflictingRename_ResolvedAndConverged()
    {
        await using var server = NewServer();
        var (alice, bob) = await ConnectAliceAndBobAsync(server);
        await using var _ = alice;
        await using var __ = bob;

        var method = await alice.CreateElementAsync();
        await alice.SetPropertyAsync(method, "name", PropertyValue.String("measureHeartbeat"));
        Assert.True((await alice.CommitAsync()).Success);
        await bob.UpdateAsync();

        // Concurrent conflicting edits.
        await alice.SetPropertyAsync(method, "name", PropertyValue.String("scanHeartbeat"));
        await bob.SetPropertyAsync(method, "name", PropertyValue.String("sampleHeartbeat"));

        // Alice publishes first; Bob cannot commit before updating.
        Assert.True((await alice.CommitAsync()).Success);
        var blocked = await bob.CommitAsync();
        Assert.False(blocked.Success);

        // Bob updates: the conflict is detected and resolved (child wins).
        var update = await bob.UpdateAsync(ResolutionStrategy.ChildWins);
        var conflict = Assert.Single(update.Conflicts);
        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(MergeConflictType.Mmc, conflict.MergeType);
        Assert.NotNull(conflict.Resolution);
        Assert.Equal("sampleHeartbeat", bob.Model.GetElement(method)!.GetProperty("name")!.SingleValue!.Content);

        // Bob publishes; Alice pulls; both replicas and the server converge.
        Assert.True((await bob.CommitAsync()).Success);
        await alice.UpdateAsync();

        Assert.Equal("sampleHeartbeat", alice.Model.GetElement(method)!.GetProperty("name")!.SingleValue!.Content);
        ModelAssert.Equivalent(alice.Model, bob.Model);
    }

    [Fact]
    public async Task ConcurrentListEditing_Converges()
    {
        await using var server = NewServer();
        var (alice, bob) = await ConnectAliceAndBobAsync(server);
        await using var _ = alice;
        await using var __ = bob;

        var element = await alice.CreateElementAsync();
        var first = await alice.AppendListItemAsync(element, "steps", PropertyValue.String("init"));
        await alice.AppendListItemAsync(element, "steps", PropertyValue.String("shutdown"));
        Assert.True((await alice.CommitAsync()).Success);
        await bob.UpdateAsync();

        // Both insert after "init" concurrently.
        await alice.InsertListItemAsync(element, "steps", PropertyValue.String("configure"), first);
        await bob.InsertListItemAsync(element, "steps", PropertyValue.String("connect"), first);

        Assert.True((await alice.CommitAsync()).Success);
        var update = await bob.UpdateAsync(ResolutionStrategy.ChildWins);
        Assert.Contains(update.Conflicts, c => c.Category == ConflictCategory.ListOrder);
        Assert.True((await bob.CommitAsync()).Success);
        await alice.UpdateAsync();

        Assert.Equal(new[] { "init", "connect", "configure", "shutdown" },
            alice.Model.GetElement(element)!.GetProperty("steps")!.ListItems.Select(i => i.Value.Content));
        ModelAssert.Equivalent(alice.Model, bob.Model);
    }

    [Fact]
    public async Task PublicSubscription_StreamsCommittedOperations()
    {
        await using var server = NewServer();
        var (alice, bob) = await ConnectAliceAndBobAsync(server);
        await using var _ = alice;
        await using var __ = bob;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = new List<Operation>();
        var listening = Task.Run(async () =>
        {
            await foreach (var op in bob.SubscribePublicAsync(cts.Token))
            {
                lock (received)
                {
                    received.Add(op);
                }

                if (received.Count >= 2)
                {
                    break;
                }
            }
        }, cts.Token);

        var element = await alice.CreateElementAsync();
        await alice.SetPropertyAsync(element, "name", PropertyValue.String("published"));
        Assert.True((await alice.CommitAsync()).Success);

        await listening;
        Assert.Equal(2, received.Count);
        Assert.Equal(OperationType.CreateElement, received[0].Type);
        Assert.Equal(OperationType.SetProperty, received[1].Type);
        Assert.Equal(element, received[0].ElementId);
    }

    [Fact]
    public async Task Awareness_ReportsBrewingConflictBeforeAnySync()
    {
        await using var server = NewServer();
        var (alice, bob) = await ConnectAliceAndBobAsync(server);
        await using var _ = alice;
        await using var __ = bob;

        var element = await alice.CreateElementAsync();
        await alice.SetPropertyAsync(element, "name", PropertyValue.String("base"));
        Assert.True((await alice.CommitAsync()).Success);
        await bob.UpdateAsync();

        await alice.SetPropertyAsync(element, "name", PropertyValue.String("alice"));
        await bob.SetPropertyAsync(element, "name", PropertyValue.String("bob"));

        // Neither side synchronized yet, but the server already knows they collide.
        var conflicts = await bob.GetConflictsWithAsync("alice");
        var conflict = Assert.Single(conflicts);
        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(ConflictCategory.SingleValue, conflict.Category);
    }

    [Fact]
    public async Task OperationsOnDeletedElementsAreRejected()
    {
        await using var server = NewServer();
        var (alice, bob) = await ConnectAliceAndBobAsync(server);
        await using var _ = alice;
        await using var __ = bob;

        var element = await alice.CreateElementAsync();
        await alice.DeleteElementAsync(element);

        await Assert.ThrowsAsync<ModelSyncException>(() =>
            alice.SetPropertyAsync(element, "name", PropertyValue.String("zombie")));
    }

    [Fact]
    public async Task FreshClientReplaysFullHistoryToCurrentState()
    {
        await using var server = NewServer();
        var (alice, bob) = await ConnectAliceAndBobAsync(server);
        await using var _ = alice;
        await using var __ = bob;

        var element = await alice.CreateElementAsync();
        await alice.SetPropertyAsync(element, "name", PropertyValue.String("v1"));
        await alice.AppendListItemAsync(element, "items", PropertyValue.String("one"));
        Assert.True((await alice.CommitAsync()).Success);

        await bob.UpdateAsync();
        await bob.AppendListItemAsync(element, "items", PropertyValue.String("two"));
        Assert.True((await bob.CommitAsync()).Success);

        // A third client checks out later and reconstructs everything by replay.
        await using var carol = await WorkspaceSession.ConnectAsync(ServerAddress, "carol", server.Server.CreateHandler());
        var carolElement = carol.Model.GetElement(element);
        Assert.NotNull(carolElement);
        Assert.Equal("v1", carolElement!.GetProperty("name")!.SingleValue!.Content);
        Assert.Equal(new[] { "one", "two" },
            carolElement.GetProperty("items")!.ListItems.Select(i => i.Value.Content));
    }
}
