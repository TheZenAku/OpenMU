// <copyright file="MUIdleJunk.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Persistence;

/// <summary>
/// What HUNT sells, and what the inventory window's "sell junk" offers (owner, 10-15: "still leaves a lot
/// of junk"). Beside the plain weapons and armor of <see cref="MUIdleSettings.IsJunk"/>: plain rings,
/// pendants and small pets, orbs and scrolls of other classes or already learned, the weaker potions
/// and event materials below the character's event level. Gear the character would put on stays (HUNT
/// wears it next, <see cref="MUIdleEquipment"/>); never sold are worn and locked items, anything
/// excellent, ancient or socketed, wings, jewels, tickets and what merchants do not buy.
/// </summary>
internal static class MUIdleJunk
{
    private const byte OrbGroup = 12;

    private const byte PetAndJewelryGroup = 13;

    private const byte PotionGroup = 14;

    private const byte ScrollGroup = 15;

    /// <summary>
    /// Guardian Angel and Imp, the rings of Ice, Poison, Fire, Earth, Wind and Magic and the pendants of
    /// Lightning, Fire, Ice, Wind, Water and Ability: plain, they are worth their price, or a slot.
    /// </summary>
    private static readonly HashSet<short> PlainJewelryAndPets = [0, 1, 8, 9, 12, 13, 21, 22, 23, 24, 25, 26, 27, 28];

    /// <summary>
    /// The potions by kind, weakest first: healing (with the apple), mana, shield, complex - and whether
    /// HUNT buys that kind at merchants (<see cref="BotShoppingHandler"/>).
    /// </summary>
    private static readonly (short[] Numbers, bool Bought)[] PotionKinds =
    [
        ([0, 1, 2, 3], true),
        ([4, 5, 6], true),
        ([35, 36, 37], false),
        ([38, 39, 40], false),
    ];

    /// <summary>Antidote and ale: nothing HUNT drinks.</summary>
    private static readonly HashSet<short> UndrunkPotions = [8, 9];

    /// <summary>
    /// The materials of the event tickets, by event (eye and key, scroll of archangel and blood bone, old
    /// scroll and covenant): their level is the event level of the ticket they make.
    /// </summary>
    private static readonly Dictionary<(byte Group, short Number), MiniGameType> EventMaterials = new()
    {
        [(14, 17)] = MiniGameType.DevilSquare,
        [(14, 18)] = MiniGameType.DevilSquare,
        [(13, 16)] = MiniGameType.BloodCastle,
        [(13, 17)] = MiniGameType.BloodCastle,
        [(13, 49)] = MiniGameType.IllusionTemple,
        [(13, 50)] = MiniGameType.IllusionTemple,
    };

    /// <summary>
    /// Determines whether an item of the bag is junk for this character.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="item">The item.</param>
    /// <param name="settings">The idle settings (the rules on and off, the locks).</param>
    /// <returns><c>true</c>, if the item is junk.</returns>
    public static bool IsJunk(Player player, Item item, MUIdleSettings settings)
    {
        if (item.Definition is not { } definition
            || item.ItemSlot < InventoryConstants.EquippableSlotsCount
            || settings.LockedItems.Contains(item.GetId())
            || !MUIdleInventory.CanBeSold(item)
            || item.IsExcellent()
            || item.IsAncient()
            || item.SocketCount > 0
            || item.IsWing())
        {
            return false;
        }

        if (settings.IsJunk(item) || IsPlainJewelryOrPet(item, definition, settings))
        {
            return !MUIdleEquipment.IsUpgrade(player, item, settings);
        }

        if (definition.Skill is { } skill && (definition.Group == OrbGroup || definition.Group == ScrollGroup))
        {
            return settings.SellSkillItems && !CanStillLearn(player, definition, skill);
        }

        if (definition.Group == PotionGroup && settings.SellWeakPotions && IsWeakPotion(player, definition.Number))
        {
            return true;
        }

        return settings.SellOldEventItems
               && EventMaterials.TryGetValue((definition.Group, definition.Number), out var eventType)
               && IsBelowEventLevel(player, item.Level, eventType);
    }

    private static bool IsPlainJewelryOrPet(Item item, ItemDefinition definition, MUIdleSettings settings)
        => definition.Group == PetAndJewelryGroup
           && PlainJewelryAndPets.Contains(definition.Number)
           && item.Level <= settings.SellMaxItemLevel
           && !item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.HarmonyOption
                                         || o.ItemOption?.OptionType == ItemOptionTypes.GuardianOption);

    /// <summary>
    /// Whether the character learns the skill one day: its class may (an empty list: every class), and it
    /// does not know it yet. Requirements it does not meet yet do not count: it grows into them.
    /// </summary>
    private static bool CanStillLearn(Player player, ItemDefinition definition, Skill skill)
    {
        if (player.SelectedCharacter?.CharacterClass is not { } characterClass)
        {
            return true;
        }

        static bool Qualifies(ICollection<CharacterClass> classes, CharacterClass characterClass)
            => classes.Count == 0 || classes.Contains(characterClass);

        return Qualifies(definition.QualifiedCharacters, characterClass)
               && Qualifies(skill.QualifiedCharacters, characterClass)
               && player.SkillList?.ContainsSkill(skill.Number.ToUnsigned()) != true;
    }

    /// <summary>
    /// Antidotes and ale; a weaker potion while stronger ones of its kind are there - for the kinds HUNT
    /// buys, as many as its potion stock (otherwise it would sell what it just bought where the merchant
    /// has no stronger one, and buy it again).
    /// </summary>
    private static bool IsWeakPotion(Player player, short number)
    {
        if (UndrunkPotions.Contains(number))
        {
            return true;
        }

        var bag = player.Inventory?.Items
            .Where(i => i.Definition is { Group: PotionGroup } && i.ItemSlot >= InventoryConstants.EquippableSlotsCount)
            .ToList() ?? [];
        foreach (var (numbers, bought) in PotionKinds)
        {
            var tier = Array.IndexOf(numbers, number);
            if (tier < 0)
            {
                continue;
            }

            var stronger = bag.Where(i => numbers.Skip(tier + 1).Contains(i.Definition!.Number)).ToList();
            return bought
                ? stronger.Sum(i => i.Durability) >= BotShoppingHandler.GetPotionStockTarget(player)
                : stronger.Count > 0;
        }

        return false;
    }

    /// <summary>
    /// Whether every event level of that material is behind the character: those it reaches no more (its
    /// level is above their range) - not those still ahead of it.
    /// </summary>
    private static bool IsBelowEventLevel(Player player, int materialLevel, MiniGameType eventType)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return false;
        }

        var special = character.IsSpecialCharacter();
        var level = (int)player.Level;
        var stillOpen = player.GameContext.Configuration.MiniGameDefinitions
            .Where(definition => definition.Type == eventType)
            .Where(definition => level <= (special ? definition.MaximumSpecialCharacterLevel : definition.MaximumCharacterLevel))
            .Select(definition => definition.TicketItemLevel)
            .ToList();
        if (stillOpen.Count == 0)
        {
            // Outgrown entirely: all of it is behind the character. An event this server does not hold: kept.
            return player.GameContext.Configuration.MiniGameDefinitions.Any(definition => definition.Type == eventType);
        }

        return materialLevel < stillOpen.Min();
    }
}
