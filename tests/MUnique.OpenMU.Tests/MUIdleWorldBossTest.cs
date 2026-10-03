// <copyright file="MUIdleWorldBossTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the reward split of <see cref="MUIdleWorldBossPlugIn"/>: by damage share, deterministic.
/// </summary>
[TestFixture]
public class MUIdleWorldBossTest
{
    /// <summary>The whole pool is paid, each by share, the rounding remainder to the top damager.</summary>
    [Test]
    public void PoolIsSplitByDamageShare()
    {
        var shares = MUIdleWorldBossPlugIn.Split(1000, new Dictionary<string, long> { ["Ana"] = 500, ["Bob"] = 300, ["Cid"] = 200 });

        Assert.That(shares.Select(s => (s.Name, s.Zen)), Is.EqualTo(new[] { ("Ana", 500), ("Bob", 300), ("Cid", 200) }));
        Assert.That(shares.Sum(s => s.Zen), Is.EqualTo(1000));
    }

    /// <summary>What rounding leaves over goes to the top damager, so nothing is lost or invented.</summary>
    [Test]
    public void RemainderGoesToTheTopDamager()
    {
        var shares = MUIdleWorldBossPlugIn.Split(100, new Dictionary<string, long> { ["Ana"] = 1, ["Bob"] = 1, ["Cid"] = 1 });

        Assert.That(shares.Sum(s => s.Zen), Is.EqualTo(100));
        Assert.That(shares[0], Is.EqualTo(("Ana", 1L, 100.0 / 3, 34)).Using<(string, long, double, int)>((a, b) => a.Item1 == b.Item1 && a.Item4 == b.Item4));
    }

    /// <summary>Equal damage is ordered by name: the same fight always pays the same way.</summary>
    [Test]
    public void TiesAreBrokenByName()
    {
        var first = MUIdleWorldBossPlugIn.Split(10, new Dictionary<string, long> { ["Zed"] = 5, ["Amy"] = 5 });
        var second = MUIdleWorldBossPlugIn.Split(10, new Dictionary<string, long> { ["Amy"] = 5, ["Zed"] = 5 });

        Assert.That(first.Select(s => s.Name), Is.EqualTo(new[] { "Amy", "Zed" }));
        Assert.That(second, Is.EqualTo(first));
    }

    /// <summary>Nobody hurt it: nothing to pay.</summary>
    [Test]
    public void NoDamageNoPayout()
    {
        Assert.That(MUIdleWorldBossPlugIn.Split(1000, new Dictionary<string, long>()), Is.Empty);
    }

    /// <summary>
    /// A QA request needs a boss; the pool and the duration stay within bounds.
    /// </summary>
    [Test]
    public void ReadsQaRequests()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleWorldBossPlugIn.ParseQaRequest("""{"boss":"Hydra"}"""), Is.EqualTo(("Hydra", (short?)null, 1000, 15)));
            Assert.That(MUIdleWorldBossPlugIn.ParseQaRequest("""{"boss":"Gorgon","map":4,"zen":99999999,"minutes":0}"""), Is.EqualTo(("Gorgon", (short?)4, 1_000_000, 1)));
            Assert.That(MUIdleWorldBossPlugIn.ParseQaRequest("""{"zen":5}"""), Is.Null);
            Assert.That(MUIdleWorldBossPlugIn.ParseQaRequest("not json"), Is.Null);
        });
    }
}
