// <copyright file="HuntMuHelperSettings.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using MUnique.OpenMU.GameLogic.MuHelper;

/// <summary>
/// The MU Helper settings an offline hunter uses: the character's own MU Helper configuration when
/// it saved one, with the guarantees HUNT mode needs on top (it always attacks something, falls back
/// to the best learned skill, drinks mana potions as well). Without a saved configuration, sensible
/// defaults apply: pick up zen, jewels, excellent and ancient items, drink potions at 50%.
/// </summary>
internal sealed class HuntMuHelperSettings : IMuHelperSettings
{
    private readonly IMuHelperSettings? _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="HuntMuHelperSettings"/> class.
    /// </summary>
    /// <param name="inner">The character's own settings, if any.</param>
    public HuntMuHelperSettings(IMuHelperSettings? inner) => this._inner = inner;

    /// <inheritdoc />
    public int BasicSkillId => this._inner?.BasicSkillId ?? 0;

    /// <inheritdoc />
    public int ActivationSkill1Id => this._inner?.ActivationSkill1Id ?? 0;

    /// <inheritdoc />
    public int ActivationSkill2Id => this._inner?.ActivationSkill2Id ?? 0;

    /// <inheritdoc />
    public int DelayMinSkill1 => this._inner?.DelayMinSkill1 ?? 0;

    /// <inheritdoc />
    public int DelayMinSkill2 => this._inner?.DelayMinSkill2 ?? 0;

    /// <inheritdoc />
    public bool Skill1UseTimer => this._inner?.Skill1UseTimer ?? false;

    /// <inheritdoc />
    public bool Skill1UseCondition => this._inner?.Skill1UseCondition ?? false;

    /// <inheritdoc />
    public bool Skill1ConditionAttacking => this._inner?.Skill1ConditionAttacking ?? false;

    /// <inheritdoc />
    public int Skill1SubCondition => this._inner?.Skill1SubCondition ?? 0;

    /// <inheritdoc />
    public bool Skill2UseTimer => this._inner?.Skill2UseTimer ?? false;

    /// <inheritdoc />
    public bool Skill2UseCondition => this._inner?.Skill2UseCondition ?? false;

    /// <inheritdoc />
    public bool Skill2ConditionAttacking => this._inner?.Skill2ConditionAttacking ?? false;

    /// <inheritdoc />
    public int Skill2SubCondition => this._inner?.Skill2SubCondition ?? 0;

    /// <inheritdoc />
    public bool UseCombo => this._inner?.UseCombo ?? false;

    /// <inheritdoc />
    public int HuntingRange => Math.Max(this._inner?.HuntingRange ?? 6, 4);

    /// <inheritdoc />
    public int MaxSecondsAway => this._inner?.MaxSecondsAway ?? 30;

    /// <inheritdoc />
    public bool LongRangeCounterAttack => this._inner?.LongRangeCounterAttack ?? false;

    /// <inheritdoc />
    public bool ReturnToOriginalPosition => this._inner?.ReturnToOriginalPosition ?? true;

    /// <inheritdoc />
    public int BuffSkill0Id => this._inner?.BuffSkill0Id ?? 0;

    /// <inheritdoc />
    public int BuffSkill1Id => this._inner?.BuffSkill1Id ?? 0;

    /// <inheritdoc />
    public int BuffSkill2Id => this._inner?.BuffSkill2Id ?? 0;

    /// <inheritdoc />
    public bool BuffOnDuration => this._inner?.BuffOnDuration ?? true;

    /// <inheritdoc />
    public bool BuffDurationForParty => this._inner?.BuffDurationForParty ?? false;

    /// <inheritdoc />
    public int BuffCastIntervalSeconds => this._inner?.BuffCastIntervalSeconds ?? 0;

    /// <inheritdoc />
    public bool AutoHeal => this._inner?.AutoHeal ?? true;

    /// <inheritdoc />
    public int HealThresholdPercent => this._inner?.HealThresholdPercent ?? 50;

    /// <inheritdoc />
    public bool UseDrainLife => this._inner?.UseDrainLife ?? false;

    /// <inheritdoc />
    public bool UseHealPotion => this._inner?.UseHealPotion ?? true;

    /// <inheritdoc />
    public int PotionThresholdPercent => this._inner?.PotionThresholdPercent is > 0 and var p ? p : 50;

    /// <inheritdoc />
    public bool SupportParty => this._inner?.SupportParty ?? false;

    /// <inheritdoc />
    public bool AutoHealParty => this._inner?.AutoHealParty ?? false;

    /// <inheritdoc />
    public int HealPartyThresholdPercent => this._inner?.HealPartyThresholdPercent ?? 50;

    /// <inheritdoc />
    public bool UseDarkRaven => this._inner?.UseDarkRaven ?? true;

    /// <inheritdoc />
    public int DarkRavenMode => this._inner?.DarkRavenMode ?? 1;

    /// <inheritdoc />
    public int ObtainRange => Math.Max(this._inner?.ObtainRange ?? 6, 3);

    /// <inheritdoc />
    public bool PickAllItems => this._inner?.PickAllItems ?? false;

    /// <inheritdoc />
    public bool PickSelectItems => this._inner?.PickSelectItems ?? true;

    /// <inheritdoc />
    public bool PickJewel => this._inner?.PickJewel ?? true;

    /// <inheritdoc />
    public bool PickZen => this._inner?.PickZen ?? true;

    /// <inheritdoc />
    public bool PickAncient => this._inner?.PickAncient ?? true;

    /// <inheritdoc />
    public bool PickExcellent => this._inner?.PickExcellent ?? true;

    /// <inheritdoc />
    public bool PickExtraItems => this._inner?.PickExtraItems ?? false;

    /// <inheritdoc />
    public IReadOnlyList<string> ExtraItemNames => this._inner?.ExtraItemNames ?? [];

    /// <inheritdoc />
    public bool RepairItem => this._inner?.RepairItem ?? false;

    /// <inheritdoc />
    public bool UseSelfDefense => this._inner?.UseSelfDefense ?? true;

    /// <inheritdoc />
    public bool AutoAcceptFriend => false;

    /// <inheritdoc />
    public bool AutoAcceptGuild => false;

    /// <inheritdoc />
    public bool FallbackBasicAttack => true;

    /// <inheritdoc />
    public bool AutoSelectBestSkill => this.BasicSkillId == 0 || (this._inner?.AutoSelectBestSkill ?? true);

    /// <inheritdoc />
    public bool AutoSelectBuffs => this._inner?.AutoSelectBuffs ?? true;

    /// <inheritdoc />
    public bool UseManaPotion => true;

    /// <inheritdoc />
    public bool OnlyHuntSafeMonsters => this._inner?.OnlyHuntSafeMonsters ?? true;

    /// <inheritdoc />
    public bool PickUpgradeItems => false;
}
