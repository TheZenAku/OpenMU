// <copyright file="MUIdleEquipment.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Persistence;

/// <summary>
/// HUNT's gear (owner, 10-15): the best piece of the bag goes on. "Best" is the character's own numbers -
/// its damage, defense and life as the game works them out with the piece on and without, in a scratch
/// attribute system of the character's stats: its class formulas, the item's level and options, the
/// excellent and ancient bonuses, sets, wings and double wield count as they do in a fight. A rating
/// from the item's tier alone (the bots' <see cref="BotEquipmentHandler"/>) put a +0 sword over a +9
/// one of the tier below.
/// Locked slots and locked items are the player's and never change; weapons stay in their family (a bow
/// for a bow, with its ammunition; a staff for a staff; melee for melee) and keep their skill; trained
/// pets, mounts, ammunition and transformation rings stay as they are.
/// </summary>
internal static class MUIdleEquipment
{
    /// <summary>
    /// The smallest gain worth a swap, on the score's log scale: 0.002 is about 0.3 % more damage, or
    /// 1 % more defense or life.
    /// </summary>
    public const double MinimumGain = 0.002;

    private const byte LastWeaponGroup = 5;

    private const byte LastMeleeGroup = 3;

    private const byte BowGroup = 4;

    private const byte ShieldGroup = 6;

    private const byte PetAndJewelryGroup = 13;

    /// <summary>Uniria, Dinorant, Dark Horse and Fenrir: a mount is the player's choice.</summary>
    private static readonly HashSet<short> MountNumbers = [2, 3, 4, 37];

    private static readonly ConditionalWeakTable<Player, Memory> Memories = new();

    /// <summary>
    /// Puts the best upgrade of the bag on - one swap a call, so a pass stays short.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="settings">The idle settings (the locks).</param>
    /// <returns>The equipment slot of the piece put on; null for no swap.</returns>
    public static async ValueTask<byte?> EquipBestAsync(Player player, MUIdleSettings settings)
    {
        if (player.Inventory is not { } inventory)
        {
            return null;
        }

        var memory = Memories.GetValue(player, _ => new Memory());
        Candidate? best;
        lock (memory)
        {
            best = memory.Best(player, inventory, settings);
        }

        if (best is null)
        {
            return null;
        }

        var replaced = string.Join(", ", best.Removed.Select(item => item.ToString()));
        if (await BotEquipmentHandler.TryApplySwapAsync(player, inventory, best.Item, new BotEquipmentHandler.EquipSwap(best.Slot, best.Removed)).ConfigureAwait(false))
        {
            player.Logger.LogInformation("MUIdle: {Player} put on {Item} (score +{Gain:F3}), off: {Replaced}.", player.Name, best.Item, best.Gain, replaced.Length == 0 ? "-" : replaced);
            return best.Slot;
        }

        // The engine refused (a requirement the old piece's bonus met): not again until something changes.
        lock (memory)
        {
            memory.Refuse(best.Item);
        }

        return null;
    }

    /// <summary>
    /// Determines whether the character would put the item on now: kept from the junk, HUNT wears it next.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="item">The item of the bag.</param>
    /// <param name="settings">The idle settings (the locks).</param>
    /// <returns><c>true</c>, if the item is an upgrade.</returns>
    public static bool IsUpgrade(Player player, Item item, MUIdleSettings settings)
    {
        if (player.Inventory is not { } inventory)
        {
            return false;
        }

        var memory = Memories.GetValue(player, _ => new Memory());
        lock (memory)
        {
            return memory.GainOf(player, inventory, item, settings) > MinimumGain;
        }
    }

    /// <summary>
    /// Rates an equipment: the logarithms of damage (by the kind the character deals), defense and life
    /// (against the damage taken), so that a gain is relative - +10 damage means much at 100 and little at
    /// 10 000 - and damage weighs most, as it is what kills faster.
    /// </summary>
    /// <param name="stats">The attribute system with the equipment on.</param>
    /// <param name="offense">The kind of damage that counts; null: the strongest one.</param>
    /// <returns>The score, and the kind of damage that counted.</returns>
    internal static (double Score, DamageType Offense) Rate(AttributeSystem stats, DamageType? offense = null)
    {
        var physical = Expected(stats, Stats.MinimumPhysBaseDmg, Stats.MaximumPhysBaseDmg)
                       * (stats[Stats.HasDoubleWield] > 0 ? 2 : 1)
                       * (stats[Stats.IsTwoHandedWeaponEquipped] > 0 ? 1 + stats[Stats.TwoHandedWeaponDamageIncrease] : 1);
        var wizardry = Expected(stats, Stats.MinimumWizBaseDmg, Stats.MaximumWizBaseDmg) * Factor(stats[Stats.WizardryAttackDamageIncrease]);
        var curse = Expected(stats, Stats.MinimumCurseBaseDmg, Stats.MaximumCurseBaseDmg) * Factor(stats[Stats.CurseAttackDamageIncrease]);
        var kind = offense ?? (physical >= wizardry && physical >= curse ? DamageType.Physical : wizardry >= curse ? DamageType.Wizardry : DamageType.Curse);
        var damage = kind switch
        {
            DamageType.Wizardry => wizardry,
            DamageType.Curse => curse,
            _ => physical,
        } * Factor(stats[Stats.AttackDamageIncrease]);
        var taken = Math.Clamp(Factor(stats[Stats.DamageReceiveDecrement]), 0.05, 1.0);
        var score = (0.6 * Math.Log(1 + damage))
                    + (0.2 * Math.Log(1 + Math.Max(0, stats[Stats.DefensePvm])))
                    + (0.2 * Math.Log(1 + (Math.Max(0, stats[Stats.MaximumHealth]) / taken)))
                    + (0.05 * Math.Log(1 + Math.Max(0, stats[Stats.DefenseRatePvm])));
        return (score, kind);
    }

    /// <summary>The damage of a hit on average: excellent hits (120 % of the maximum), critical hits (the maximum), else between.</summary>
    private static double Expected(AttributeSystem stats, AttributeDefinition minimum, AttributeDefinition maximum)
    {
        double low = Math.Max(0, stats[minimum]);
        var high = Math.Max(low, stats[maximum]);
        double excellent = Math.Clamp(stats[Stats.ExcellentDamageChance], 0, 1);
        double critical = Math.Clamp(stats[Stats.CriticalDamageChance], 0, 1);
        return ((1 - excellent) * (((1 - critical) * (low + high) / 2) + (critical * high))) + (excellent * 1.2 * high);
    }

    /// <summary>A multiplier, 1 where the attribute is not set.</summary>
    private static double Factor(float value) => value > 0 ? value : 1;

    /// <summary>
    /// The slots HUNT considers for a piece: a weapon only goes into the main hand and a shield only into
    /// the other one (a second weapon there is the player's choice); anything else into any of its slots.
    /// </summary>
    private static IEnumerable<byte> TargetSlots(ItemDefinition definition)
    {
        var slots = definition.ItemSlot!.ItemSlots.Select(s => (byte)s).ToList();
        if (definition.Group <= LastWeaponGroup && slots.Contains(InventoryConstants.LeftHandSlot))
        {
            return [InventoryConstants.LeftHandSlot];
        }

        if (definition.Group == ShieldGroup && slots.Contains(InventoryConstants.RightHandSlot))
        {
            return [InventoryConstants.RightHandSlot];
        }

        return slots;
    }

    /// <summary>Trained pets, mounts and transformation rings: put on and taken off by the player only.</summary>
    private static bool IsKeptAsIs(Item item)
        => item.IsTrainablePet()
           || (item.Definition is { Group: PetAndJewelryGroup } definition && MountNumbers.Contains(definition.Number))
           || item.IsTransformationRing();

    /// <summary>
    /// Whether the piece fits the hand: a weapon replaces one of its family and keeps its skill (a Blade
    /// Knight's combo starts with a weapon skill), a bow goes in only with its ammunition in the other hand,
    /// a shield only where nothing or a shield is.
    /// </summary>
    private static bool FitsTheHand(IStorage inventory, Item item, ItemDefinition definition, byte slot, Item? current)
    {
        if (slot > InventoryConstants.RightHandSlot)
        {
            return true;
        }

        var currentDefinition = current?.Definition;
        if (definition.Group > LastWeaponGroup)
        {
            return currentDefinition is null || currentDefinition.Group == ShieldGroup;
        }

        if (currentDefinition is { Group: <= LastWeaponGroup })
        {
            // The group is enough (a one-handed staff and a two-handed one are of one family): a bow and a
            // crossbow, both group 4, go into different hands, and the other hand's ammunition stays.
            var sameFamily = (definition.Group <= LastMeleeGroup && currentDefinition.Group <= LastMeleeGroup)
                             || definition.Group == currentDefinition.Group;
            var keepsSkill = !(current!.HasSkill && currentDefinition.Skill is not null) || (item.HasSkill && definition.Skill is not null);
            return sameFamily && keepsSkill;
        }

        if (currentDefinition is not null)
        {
            return false;
        }

        if (definition.Group == BowGroup)
        {
            var otherHand = slot == InventoryConstants.LeftHandSlot ? InventoryConstants.RightHandSlot : InventoryConstants.LeftHandSlot;
            return inventory.GetItem(otherHand)?.Definition?.IsAmmunition == true;
        }

        return true;
    }

    /// <summary>
    /// The ways the piece could go on: into which slot, and what comes off for it (the piece there, and
    /// what blocks the other hand of a two-handed weapon).
    /// </summary>
    private static IEnumerable<(byte Slot, List<Item> Removed)> PlansFor(Player player, IStorage inventory, Item item, MUIdleSettings settings, HashSet<Guid> lockedItems)
    {
        if (item.Definition is not { ItemSlot.ItemSlots.Count: > 0 } definition
            || player.SelectedCharacter?.CharacterClass is not { } characterClass
            || definition.IsAmmunition
            || !definition.QualifiedCharacters.Contains(characterClass)
            || lockedItems.Contains(item.GetId())
            || IsKeptAsIs(item)
            || (definition.Durability > 0 && item.Durability <= 0)
            || !player.CompliesRequirements(item))
        {
            yield break;
        }

        bool IsFixed(Item equipped) => lockedItems.Contains(equipped.GetId()) || IsKeptAsIs(equipped) || equipped.Definition?.IsAmmunition == true;

        foreach (var slot in TargetSlots(definition))
        {
            var current = inventory.GetItem(slot);
            if (settings.LockedEquipSlots.Contains(slot)
                || (current is not null && IsFixed(current))
                || !FitsTheHand(inventory, item, definition, slot, current))
            {
                continue;
            }

            var removed = new List<Item>(2);
            if (current is not null)
            {
                removed.Add(current);
            }

            if (definition.ConflictsWithEquippedHands(inventory, slot))
            {
                // Only a two-handed weapon for the main hand takes the other hand's piece off with it.
                if (slot != InventoryConstants.LeftHandSlot
                    || settings.LockedEquipSlots.Contains(InventoryConstants.RightHandSlot)
                    || inventory.GetItem(InventoryConstants.RightHandSlot) is not { } blocking
                    || IsFixed(blocking))
                {
                    continue;
                }

                removed.Add(blocking);
            }

            yield return (slot, removed);
        }
    }

    /// <summary>A piece of the bag worth putting on: where, what comes off, and what it adds to the score.</summary>
    private sealed record Candidate(Item Item, byte Slot, IReadOnlyList<Item> Removed, double Gain);

    /// <summary>
    /// What HUNT worked out for a character, kept while its gear, stats and locks stay the same: each item
    /// is rated once, not every few seconds (a pass rates the bag, the junk rule asks per item).
    /// </summary>
    private sealed class Memory
    {
        private readonly Dictionary<(Guid Id, byte Level, bool Intact, bool Skill, int Options), Candidate?> _candidates = [];

        private readonly HashSet<Guid> _refused = [];

        private string _signature = string.Empty;

        private Evaluator? _evaluator;

        public Candidate? Best(Player player, IStorage inventory, MUIdleSettings settings)
        {
            this.Refresh(player, inventory, settings);
            Candidate? best = null;
            foreach (var item in inventory.Items.ToList())
            {
                if (item.ItemSlot < InventoryConstants.EquippableSlotsCount
                    || item.ItemSlot >= player.InventorySize
                    || this._refused.Contains(item.GetId())
                    || this.CandidateOf(player, inventory, item, settings) is not { } candidate
                    || candidate.Gain <= MinimumGain)
                {
                    continue;
                }

                if (best is null || candidate.Gain > best.Gain)
                {
                    best = candidate;
                }
            }

            return best;
        }

        public double GainOf(Player player, IStorage inventory, Item item, MUIdleSettings settings)
        {
            this.Refresh(player, inventory, settings);
            return this.CandidateOf(player, inventory, item, settings)?.Gain ?? 0;
        }

        public void Refuse(Item item) => this._refused.Add(item.GetId());

        private Candidate? CandidateOf(Player player, IStorage inventory, Item item, MUIdleSettings settings)
        {
            var key = (item.GetId(), item.Level, item.Durability > 0, item.HasSkill, item.ItemOptions.Count);
            if (this._candidates.TryGetValue(key, out var known))
            {
                return known;
            }

            Candidate? best = null;
            var lockedItems = settings.LockedItems.ToHashSet();
            foreach (var (slot, removed) in PlansFor(player, inventory, item, settings, lockedItems))
            {
                this._evaluator ??= new Evaluator(player, inventory);
                var gain = this._evaluator.Gain(item, slot, removed);
                if (best is null || gain > best.Gain)
                {
                    best = new Candidate(item, slot, removed, gain);
                }
            }

            this._candidates[key] = best;
            return best;
        }

        /// <summary>Forgets what it worked out once the gear, the stats or the locks changed.</summary>
        private void Refresh(Player player, IStorage inventory, MUIdleSettings settings)
        {
            var signature = Signature(player, inventory, settings);
            if (signature == this._signature)
            {
                return;
            }

            this._signature = signature;
            this._candidates.Clear();
            this._refused.Clear();
            this._evaluator = null;
        }

        private static string Signature(Player player, IStorage inventory, MUIdleSettings settings)
        {
            var text = new StringBuilder();
            foreach (var attribute in player.SelectedCharacter?.Attributes ?? [])
            {
                text.Append(attribute.Value).Append(',');
            }

            text.Append('|');
            for (byte slot = InventoryConstants.FirstEquippableItemSlotIndex; slot <= InventoryConstants.LastEquippableItemSlotIndex; slot++)
            {
                if (inventory.GetItem(slot) is { } item)
                {
                    text.Append(slot).Append(':').Append(item.GetId()).Append(':').Append(item.Level).Append(':').Append(item.Durability > 0).Append(';');
                }
            }

            text.Append('|').AppendJoin(',', settings.LockedEquipSlots.Order());
            text.Append('|').AppendJoin(',', settings.LockedItems.Order());
            return text.ToString();
        }
    }

    /// <summary>
    /// Rates equipments of a character: a fresh attribute system of copies of the character's stats with
    /// the class formulas, plus the power-ups of each piece - never the character's own attribute system,
    /// which every power-up joins as it is made (and which the fight reads meanwhile).
    /// </summary>
    private sealed class Evaluator
    {
        private readonly CharacterClass _class;

        private readonly GameConfiguration _configuration;

        private readonly IItemPowerUpFactory _factory;

        private readonly (AttributeDefinition Definition, float Value)[] _stats;

        private readonly Dictionary<byte, Item> _equipped;

        private readonly double _current;

        private readonly DamageType _offense;

        public Evaluator(Player player, IStorage inventory)
        {
            var character = player.SelectedCharacter!;
            this._class = character.CharacterClass!;
            this._configuration = player.GameContext.Configuration;
            this._factory = player.GameContext.ItemPowerUpFactory;
            this._stats = character.Attributes.Concat(player.Account?.Attributes ?? [])
                .Where(attribute => attribute.Definition is not null)
                .DistinctBy(attribute => attribute.Definition)
                .Select(attribute => (attribute.Definition!, attribute.Value))
                .ToArray();
            this._equipped = [];
            for (byte slot = InventoryConstants.FirstEquippableItemSlotIndex; slot <= InventoryConstants.LastEquippableItemSlotIndex; slot++)
            {
                if (inventory.GetItem(slot) is { } item)
                {
                    this._equipped[slot] = item;
                }
            }

            (this._current, this._offense) = Rate(this.Build(this._equipped));
        }

        /// <summary>What the score gains with the item in the slot and the removed pieces off.</summary>
        public double Gain(Item item, byte slot, IReadOnlyList<Item> removed)
        {
            var equipment = new Dictionary<byte, Item>(this._equipped);
            foreach (var piece in removed)
            {
                equipment.Remove(piece.ItemSlot);
            }

            // A copy in the slot: the power-ups of a piece only count where it is worn, and the bag item
            // itself stays untouched.
            var copy = new TemporaryItem { ItemSlot = slot };
            copy.AssignValues(item);
            equipment[slot] = copy;
            return Rate(this.Build(equipment), this._offense).Score - this._current;
        }

        private AttributeSystem Build(IReadOnlyDictionary<byte, Item> equipment)
        {
            var stats = new AttributeSystem(
                this._stats.Select(stat => (IAttribute)new StatAttribute(stat.Definition, stat.Value)),
                this._class.BaseAttributeValues.Concat(this._configuration.GlobalBaseAttributeValues),
                this._class.AttributeCombinations.Concat(this._configuration.GlobalAttributeCombinations));

            // A power-up joins the system it is made for, so making them is all there is to do.
            foreach (var item in equipment.Values)
            {
                foreach (var unused in this._factory.GetPowerUps(item, stats))
                {
                }
            }

            foreach (var unused in this._factory.GetSetPowerUps(equipment.Values, stats, this._configuration))
            {
            }

            return stats;
        }
    }
}
