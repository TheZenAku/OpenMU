// <copyright file="RatedDropGenerator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// Applies the MUIdle item drop rate and zen rate (<see cref="MUIdleConfiguration"/>, editable in the
/// admin panel at runtime) on top of the upstream drop generator. The rates are server-side only.
/// </summary>
/// <remarks>
/// Item drop rate: below 1 each dropped item survives with that probability; above 1 the monster rolls
/// its drops again (floor(rate) - 1 extra rolls plus one more with the fractional chance).
/// Zen rate: multiplies the dropped zen.
/// </remarks>
public sealed class RatedDropGenerator : IDropGenerator
{
    private const int MaximumExtraRolls = 9;

    private readonly IDropGenerator _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="RatedDropGenerator"/> class.
    /// </summary>
    /// <param name="inner">The upstream generator.</param>
    public RatedDropGenerator(IDropGenerator inner) => this._inner = inner;

    /// <inheritdoc />
    public async ValueTask<(IEnumerable<Item> Items, uint? Money)> GenerateItemDropsAsync(MonsterDefinition monster, int gainedExperience, Player player)
    {
        var (items, money) = await this._inner.GenerateItemDropsAsync(monster, gainedExperience, player).ConfigureAwait(false);
        var configuration = MUIdleSessions.GetConfiguration(player.GameContext);
        var dropRate = Math.Max(0, configuration.ItemDropRate);
        var zenRate = Math.Max(0, configuration.ZenRate);

        if (money is { } amount && Math.Abs(zenRate - 1) > 0.0001)
        {
            var rated = Math.Min(uint.MaxValue, Math.Round(amount * zenRate));
            money = rated >= 1 ? (uint)rated : null;
        }

        if (Math.Abs(dropRate - 1) < 0.0001)
        {
            return (items, money);
        }

        if (dropRate < 1)
        {
            return (items.Where(_ => Random.Shared.NextDouble() < dropRate).ToList(), money);
        }

        var extraRolls = Math.Min(MaximumExtraRolls, (int)Math.Floor(dropRate) - 1 + (Random.Shared.NextDouble() < dropRate - Math.Floor(dropRate) ? 1 : 0));
        if (extraRolls <= 0)
        {
            return (items, money);
        }

        var all = items.ToList();
        for (var i = 0; i < extraRolls; i++)
        {
            // The extra rolls add items only; the zen of a kill is rated above, once.
            var (extra, _) = await this._inner.GenerateItemDropsAsync(monster, gainedExperience, player).ConfigureAwait(false);
            all.AddRange(extra);
        }

        return (all, money);
    }

    /// <inheritdoc />
    public Item? GenerateItemDrop(DropItemGroup group) => this._inner.GenerateItemDrop(group);

    /// <inheritdoc />
    public (Item? Item, uint? Money, ItemDropEffect DropEffect) GenerateItemDrop(IEnumerable<DropItemGroup> groups) => this._inner.GenerateItemDrop(groups);
}
