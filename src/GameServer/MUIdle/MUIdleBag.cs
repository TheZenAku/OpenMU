// <copyright file="MUIdleBag.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.ItemConsumeActions;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Persistence;

/// <summary>
/// HUNT's care of the bag, every few seconds on the MU Helper tick (<see cref="GameLogic.Bots.BotNavigatorPolicy.TendInventory"/>):
/// orbs and scrolls the character can learn are read, the best gear goes on (<see cref="MUIdleEquipment"/>)
/// and the junk is sold where the character stands (<see cref="MUIdleJunk"/>). The settings are read
/// fresh each time: a lock set in the window counts at once, without restarting the hunt.
/// </summary>
internal static class MUIdleBag
{
    private const byte OrbGroup = 12;

    private const byte ScrollGroup = 15;

    private static readonly ItemConsumeAction ConsumeAction = new();

    /// <summary>
    /// Tends the bag of a hunting character.
    /// </summary>
    /// <param name="hunter">The hunter.</param>
    /// <param name="huntSettings">The settings the hunt started with (an offline hunter has no others).</param>
    public static async ValueTask TendAsync(IHunter hunter, MUIdleSettings huntSettings)
    {
        var player = hunter.Player;
        var settings = MUIdleHuntTracker.GetSettings(player) ?? huntSettings;
        try
        {
            if (settings.AutoSkills)
            {
                await LearnSkillItemsAsync(player, settings).ConfigureAwait(false);
            }

            if (settings.AutoEquip
                && await MUIdleEquipment.EquipBestAsync(player, settings).ConfigureAwait(false) is { } slot
                && player is RemotePlayer remote)
            {
                await MUIdleInventory.SendEquippedAsync(remote, slot).ConfigureAwait(false);
            }

            if (settings.AutoSell)
            {
                await MUIdleInventory.SellJunkHereAsync(player, settings).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            player.Logger.LogError(ex, "MUIdle: tending the bag of {Player} failed.", player.Name);
        }
    }

    /// <summary>
    /// Reads the orbs, scrolls and parchments of the bag the character can learn now - as the player would
    /// with a right click; the requirements are the same.
    /// </summary>
    private static async ValueTask LearnSkillItemsAsync(Player player, MUIdleSettings settings)
    {
        if (player.Inventory is not { } inventory || player.SelectedCharacter?.CharacterClass is not { } characterClass)
        {
            return;
        }

        static bool Qualifies(ICollection<CharacterClass> classes, CharacterClass characterClass)
            => classes.Count == 0 || classes.Contains(characterClass);

        var learnable = inventory.Items
            .Where(item => item.ItemSlot >= InventoryConstants.EquippableSlotsCount
                           && item.Definition is { Skill: { } skill } definition
                           && (definition.Group == OrbGroup || definition.Group == ScrollGroup)
                           && !settings.LockedItems.Contains(item.GetId())
                           && Qualifies(definition.QualifiedCharacters, characterClass)
                           && Qualifies(skill.QualifiedCharacters, characterClass)
                           && player.SkillList?.ContainsSkill(skill.Number.ToUnsigned()) == false
                           && player.CompliesRequirements(item))
            .ToList();
        foreach (var item in learnable)
        {
            var skill = item.Definition!.Skill!;
            if (player.SkillList?.ContainsSkill(skill.Number.ToUnsigned()) != false)
            {
                continue; // two orbs of one skill: the first taught it.
            }

            var slot = item.ItemSlot;
            await ConsumeAction.HandleConsumeRequestAsync(player, slot, byte.MaxValue, FruitUsage.Undefined).ConfigureAwait(false);
            if (inventory.GetItem(slot) != item)
            {
                player.Logger.LogInformation("MUIdle: HUNT of {Player} learned {Skill} from {Item}.", player.Name, skill.Name, item.Definition.Name);
            }
        }
    }
}
