// <copyright file="HuntMuHelperSettingsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;
using MUnique.OpenMU.GameServer.RemoteView.MuHelper;

/// <summary>
/// Tests the MU Helper settings HUNT runs with (<see cref="HuntMuHelperSettings"/>): the automatic skills
/// of HUNT v2 against the slots of the character's own MU Helper configuration.
/// </summary>
[TestFixture]
public class HuntMuHelperSettingsTest
{
    /// <summary>
    /// With the automatic skills (the default) the slots rest: the engine picks skills, combo and buffs,
    /// in the player's order.
    /// </summary>
    [Test]
    public void AutomaticSkillsLeaveTheSlotsAside()
    {
        var own = new MuHelperSettings { BasicSkillId = 41, ActivationSkill1Id = 42, UseCombo = true, BuffSkill0Id = 7 };
        var settings = new HuntMuHelperSettings(own, new MUIdleSettings { SkillOrder = [43, 41] });

        Assert.Multiple(() =>
        {
            Assert.That(settings.BasicSkillId, Is.Zero);
            Assert.That(settings.ActivationSkill1Id, Is.Zero);
            Assert.That(settings.UseCombo, Is.False);
            Assert.That(settings.AutoCombo, Is.True);
            Assert.That(settings.AutoSelectBestSkill, Is.True);
            Assert.That(settings.AutoSelectBuffs, Is.True);
            Assert.That(settings.SkillPriority, Is.EqualTo(new[] { 43, 41 }));
        });
    }

    /// <summary>
    /// Without them the MU Helper configuration decides, its own combo included.
    /// </summary>
    [Test]
    public void ManualSkillsFollowTheConfiguration()
    {
        var own = new MuHelperSettings { BasicSkillId = 41, ActivationSkill1Id = 42, UseCombo = true, BuffSkill0Id = 7 };
        var settings = new HuntMuHelperSettings(own, new MUIdleSettings { AutoSkills = false, SkillOrder = [43] });

        Assert.Multiple(() =>
        {
            Assert.That(settings.BasicSkillId, Is.EqualTo(41));
            Assert.That(settings.ActivationSkill1Id, Is.EqualTo(42));
            Assert.That(settings.UseCombo, Is.True);
            Assert.That(settings.AutoCombo, Is.False);
            Assert.That(settings.AutoSelectBuffs, Is.False);
            Assert.That(settings.SkillPriority, Is.Empty);
        });
    }
}
