// <copyright file="MUIdleItemNamesPlugInSeason6.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Globalization;
using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// MUIdle (D31): item names the season 6 data misspells, corrected in a configuration that was
/// initialized with them. The Dark Lord's set 51 is "Paewang" (Korean; "Royal" in the English
/// client) on four pieces and "Phaewang" on the boots, so a search for the set never found them.
/// </summary>
[PlugIn]
[Display(Name = PlugInName, Description = PlugInDescription)]
[Guid("9C4E7A13-2B8D-4F6A-B0E5-71D3C8A2F946")]
public class MUIdleItemNamesPlugInSeason6 : UpdatePlugInBase
{
    /// <summary>The plug in name.</summary>
    internal const string PlugInName = "MUIdle: item name fixes";

    /// <summary>The plug in description.</summary>
    internal const string PlugInDescription = "Corrects misspelled item names (Phaewang Boots -> Paewang Boots).";

    /// <summary>The corrections: group, number, the misspelled name, the right one.</summary>
    internal static readonly IReadOnlyList<(byte Group, short Number, string Wrong, string Right)> Corrections =
    [
        (11, 51, "Phaewang Boots", "Paewang Boots"),
    ];

    /// <inheritdoc />
    public override string Name => PlugInName;

    /// <inheritdoc />
    public override string Description => PlugInDescription;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override bool IsMandatory => true;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 8, 19, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// Renames the misspelled items; an item already renamed (or by hand) is left as it is.
    /// </summary>
    /// <param name="items">The item definitions of the configuration.</param>
    /// <returns>How many were renamed.</returns>
    public static int Correct(IEnumerable<ItemDefinition> items)
    {
        var renamed = 0;
        foreach (var item in items)
        {
            foreach (var (group, number, wrong, right) in Corrections)
            {
                // The neutral (English) part only: translations added in the Admin Panel stay.
                if (item.Group == group && item.Number == number && item.Name.ValueInNeutralLanguage == wrong)
                {
                    item.Name = item.Name.WithTranslation(CultureInfo.GetCultureInfo(LocalizedString.NeutralLanguageCode), right);
                    renamed++;
                }
            }
        }

        return renamed;
    }

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        Correct(gameConfiguration.Items);
        return ValueTask.CompletedTask;
    }
}
