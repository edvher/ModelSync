using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

public class OperationTreeTests
{
    [Fact]
    public void BranchesStartAtThePublicHead()
    {
        var tree = new OperationTree();
        var op1 = Op.Create("e1");
        tree.Append("P", op1);

        tree.EnsureBranch("A");
        Assert.Equal(op1.Id, tree.Head("A"));
        Assert.Equal(op1.Id, tree.Head("P"));
    }

    [Fact]
    public void PathsAndLcaReflectBranching()
    {
        var tree = new OperationTree();
        var shared = Op.Create("e1");
        tree.Append("P", shared);

        tree.EnsureBranch("A");
        tree.EnsureBranch("B");

        var aOp = Op.Set("e1", "name", "fromA");
        var bOp = Op.Set("e1", "name", "fromB");
        tree.Append("A", aOp);
        tree.Append("B", bOp);

        Assert.Equal(shared.Id, tree.Lca("A", "B"));
        Assert.Equal(shared.Id, tree.Lca("A", "P"));

        Assert.Equal(new[] { shared.Id, aOp.Id }, tree.PathFromRoot("A").Select(o => o.Id));
        Assert.Equal(new[] { aOp.Id }, tree.PathBetween(shared.Id, tree.Head("A")).Select(o => o.Id));
        Assert.Empty(tree.PathBetween(tree.Head("P"), tree.Head("P")));
    }

    [Fact]
    public void ReattachMovesABranchSegment()
    {
        var tree = new OperationTree();
        var baseOp = Op.Create("e1");
        tree.Append("P", baseOp);

        tree.EnsureBranch("A");
        var childOp = Op.Set("e1", "name", "child");
        tree.Append("A", childOp);

        var publicOp = Op.Set("e1", "name", "public");
        tree.Append("P", publicOp);

        tree.Reattach(childOp.Id, publicOp.Id);

        Assert.Equal(new[] { baseOp.Id, publicOp.Id, childOp.Id }, tree.PathFromRoot("A").Select(o => o.Id));
        Assert.Equal(publicOp.Id, tree.Lca("A", "P"));
    }

    [Fact]
    public void ReattachRejectsCycles()
    {
        var tree = new OperationTree();
        var op1 = Op.Create("e1");
        var op2 = Op.Set("e1", "name", "x");
        tree.Append("P", op1);
        tree.Append("P", op2);

        Assert.Throws<InvalidOperationException>(() => tree.Reattach(op1.Id, op2.Id));
    }

    [Fact]
    public void SnapshotContainsHeadsAndParentage()
    {
        var tree = new OperationTree();
        var op1 = Op.Create("e1");
        tree.Append("P", op1);
        tree.EnsureBranch("A");
        var op2 = Op.Set("e1", "name", "x");
        tree.Append("A", op2);

        var snapshot = tree.Snapshot();
        Assert.Equal(op1.Id, snapshot.Heads["P"]);
        Assert.Equal(op2.Id, snapshot.Heads["A"]);
        Assert.Equal(op1.Id, snapshot.Nodes[op2.Id].Parent);
        Assert.Contains(op2.Id, snapshot.Nodes[op1.Id].Children);
    }
}
