// <copyright file="AutomaticComboTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.Offline;

using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Offline;
using MasterSkillDefinition = MUnique.OpenMU.Persistence.BasicModel.MasterSkillDefinition;
using Skill = MUnique.OpenMU.Persistence.BasicModel.Skill;
using SkillComboDefinition = MUnique.OpenMU.Persistence.BasicModel.SkillComboDefinition;
using SkillComboStep = MUnique.OpenMU.Persistence.BasicModel.SkillComboStep;

/// <summary>
/// Tests the combo HUNT chains out of the learned skills (<see cref="CombatHandler.ChainCombo"/>, HUNT v2):
/// the Blade Knight's combo of the Season 6 data - a weapon skill, then two different finishers.
/// </summary>
[TestFixture]
public class AutomaticComboTest
{
    private readonly Skill _slash = new() { Number = 21, Name = "Slash" };
    private readonly Skill _cyclone = new() { Number = 22, Name = "Cyclone" };
    private readonly Skill _twistingSlash = new() { Number = 41, Name = "Twisting Slash" };
    private readonly Skill _ragefulBlow = new() { Number = 42, Name = "Rageful Blow" };
    private readonly Skill _deathStab = new() { Number = 43, Name = "Death Stab" };

    /// <summary>
    /// The strongest skill of each step, and a finisher which is not the one of the step before.
    /// </summary>
    [Test]
    public void StrongestSkillsWithDifferentFinishers()
    {
        var scores = new Dictionary<int, float> { [21] = 10, [22] = 20, [41] = 50, [42] = 40, [43] = 30 };
        var chain = CombatHandler.ChainCombo(this.BladeKnightCombo(), Entries(this._slash, this._cyclone, this._twistingSlash, this._ragefulBlow, this._deathStab), [], s => scores[s.Number]);

        Assert.That(chain, Is.EqualTo(new[] { 22, 41, 42 }));
    }

    /// <summary>
    /// The player's order goes before the score.
    /// </summary>
    [Test]
    public void ThePlayersOrderComesFirst()
    {
        var scores = new Dictionary<int, float> { [21] = 10, [22] = 20, [41] = 50, [42] = 40, [43] = 30 };
        var chain = CombatHandler.ChainCombo(this.BladeKnightCombo(), Entries(this._slash, this._cyclone, this._twistingSlash, this._ragefulBlow, this._deathStab), [43, 21, 42], s => scores[s.Number]);

        Assert.That(chain, Is.EqualTo(new[] { 21, 43, 42 }));
    }

    /// <summary>
    /// Without a weapon skill (it comes with the weapon) there is no combo.
    /// </summary>
    [Test]
    public void NoWeaponSkillNoCombo()
    {
        var chain = CombatHandler.ChainCombo(this.BladeKnightCombo(), Entries(this._twistingSlash, this._ragefulBlow, this._deathStab), [], _ => 1);

        Assert.That(chain, Is.Empty);
    }

    /// <summary>
    /// A single finisher cannot finish: the last step must differ from the one before it.
    /// </summary>
    [Test]
    public void OneFinisherIsNotEnough()
    {
        var chain = CombatHandler.ChainCombo(this.BladeKnightCombo(), Entries(this._slash, this._twistingSlash), [], _ => 1);

        Assert.That(chain, Is.Empty);
    }

    /// <summary>
    /// A master skill which strengthens a finisher stands in for it.
    /// </summary>
    [Test]
    public void MasterSkillStandsInForItsBase()
    {
        var strengthened = new Skill { Number = 330, Name = "Twisting Slash Strengthener" };
        strengthened.MasterDefinition = new MasterSkillDefinition { ReplacedSkill = this._twistingSlash };
        var scores = new Dictionary<int, float> { [21] = 10, [330] = 90, [42] = 40 };
        var chain = CombatHandler.ChainCombo(this.BladeKnightCombo(), Entries(this._slash, strengthened, this._ragefulBlow), [], s => scores[s.Number]);

        Assert.That(chain, Is.EqualTo(new[] { 21, 330, 42 }));
    }

    private static List<SkillEntry> Entries(params Skill[] skills) => skills.Select(s => new SkillEntry { Skill = s }).ToList();

    private SkillComboDefinition BladeKnightCombo()
    {
        var combo = new SkillComboDefinition { MaximumCompletionTime = TimeSpan.FromSeconds(3) };
        void Add(Skill skill, int order, bool final = false) => combo.Steps.Add(new SkillComboStep { Skill = skill, Order = order, IsFinalStep = final });

        Add(this._slash, 1);
        Add(this._cyclone, 1);
        Add(this._twistingSlash, 2);
        Add(this._ragefulBlow, 2);
        Add(this._deathStab, 2);
        Add(this._twistingSlash, 3, true);
        Add(this._ragefulBlow, 3, true);
        Add(this._deathStab, 3, true);
        return combo;
    }
}
