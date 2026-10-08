// <copyright file="MUIdleFreeHuntTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Tests for <see cref="MUIdleFreeHuntPlugInSeason6"/> (D29): the MU Helper costs no zen.
/// </summary>
[TestFixture]
public class MUIdleFreeHuntTest
{
    /// <summary>
    /// The stored configuration (written with reference handling) keeps its stages and metadata, every
    /// cost at 0, and the rest as it was.
    /// </summary>
    [Test]
    public void StoredCostsBecomeZero()
    {
        const string stored = """
            {
              "$id": "",
              "CostPerStage": { "$id": "", "$values": [ 20, 50, 80, 100, 120 ] },
              "PayInterval": "00:05:00",
              "StageInterval": "03:20:00",
              "MinLevel": 1,
              "MaxLevel": 400
            }
            """;

        var free = JsonNode.Parse(MUIdleFreeHuntPlugInSeason6.Free(stored))!.AsObject();

        Assert.That(free["CostPerStage"]!["$values"]!.AsArray().Select(c => c!.GetValue<int>()), Is.EqualTo(new[] { 0, 0, 0, 0, 0 }));
        Assert.That(free["CostPerStage"]!["$id"], Is.Not.Null);
        Assert.That(free["PayInterval"]!.GetValue<string>(), Is.EqualTo("00:05:00"));
        Assert.That(free["MaxLevel"]!.GetValue<int>(), Is.EqualTo(400));
    }

    /// <summary>
    /// A plain array of costs (written without reference handling) becomes zeros too.
    /// </summary>
    [Test]
    public void PlainCostsBecomeZero()
    {
        var free = JsonSerializer.Deserialize<MuHelperConfiguration>(MUIdleFreeHuntPlugInSeason6.Free("""{ "CostPerStage": [ 20, 50 ], "MinLevel": 1 }"""))!;

        Assert.That(free.CostPerStage, Is.EqualTo(new[] { 0, 0 }));
    }

    /// <summary>
    /// A configuration never stored becomes the default one, free.
    /// </summary>
    [Test]
    public void MissingConfigurationIsTheDefaultFree()
    {
        var free = JsonSerializer.Deserialize<MuHelperConfiguration>(MUIdleFreeHuntPlugInSeason6.Free(null))!;

        Assert.That(free.CostPerStage, Has.Count.EqualTo(new MuHelperConfiguration().CostPerStage.Count));
        Assert.That(free.CostPerStage, Is.All.EqualTo(0));
        Assert.That(free.PayInterval, Is.EqualTo(new MuHelperConfiguration().PayInterval));
    }
}
