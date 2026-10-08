// <copyright file="MUIdleStackSizesPlugInSeason6.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// MUIdle (D28): the potions and the jewels stack up to 255 pieces, where the season 6 data stacks the
/// potions to 3 and the jewels not at all. An item's durability is its number of pieces when it is not
/// wearable, so the stack size is the definition's durability; a drop is still one piece
/// (<c>GetMaximumDurabilityOfOnePiece</c>), the price counts the pieces, and the inventory can split a
/// stack again (MUIdleStacks) for the mixes which take a set number of jewels.
/// </summary>
[PlugIn]
[Display(Name = PlugInName, Description = PlugInDescription)]
[Guid("6E1B4A92-7C3D-4F85-9A2E-3D8C5B1F7A64")]
public class MUIdleStackSizesPlugInSeason6 : UpdatePlugInBase
{
    /// <summary>The plug in name.</summary>
    internal const string PlugInName = "MUIdle: potions and jewels stack";

    /// <summary>The plug in description.</summary>
    internal const string PlugInDescription = "Potions and jewels stack up to 255 pieces in the inventory.";

    /// <summary>The pieces one stack holds at most (the item's durability is a byte).</summary>
    internal const byte StackSize = 255;

    /// <summary>The potions and jewels which stack, by item group.</summary>
    internal static readonly IReadOnlyDictionary<byte, int[]> Stacking = new Dictionary<byte, int[]>
    {
        // Apple, healing and mana potions, antidote, shield and complex potions; the jewels of bless, soul,
        // life and creation, the jewel of guardian, the gemstone, the jewel of harmony, the refine stones.
        [14] = [0, 1, 2, 3, 4, 5, 6, 8, 35, 36, 37, 38, 39, 40, 13, 14, 16, 22, 31, 41, 42, 43, 44],

        // The jewel of chaos.
        [12] = [15],
    };

    /// <inheritdoc />
    public override string Name => PlugInName;

    /// <inheritdoc />
    public override string Description => PlugInDescription;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override bool IsMandatory => true;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        foreach (var item in gameConfiguration.Items)
        {
            if (item.ItemSlot is null
                && Stacking.TryGetValue(item.Group, out var numbers)
                && numbers.Contains(item.Number))
            {
                item.Durability = StackSize;
            }
        }

        return ValueTask.CompletedTask;
    }
}
