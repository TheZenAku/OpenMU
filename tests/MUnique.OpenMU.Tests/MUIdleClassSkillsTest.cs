// <copyright file="MUIdleClassSkillsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameServer.MUIdle;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests which skills a class gets from the start (<see cref="MUIdleClassSkills"/>, D22).
/// </summary>
[TestFixture]
public class MUIdleClassSkillsTest
{
    /// <summary>
    /// The skills of the scrolls, orbs and parchments the class may use, the Summoning Orb's per level -
    /// not the skills worn gear lends, not other classes' skills, not master skills.
    /// </summary>
    [Test]
    public void TheClassGetsWhatItsScrollsAndOrbsTeach()
    {
        var wizard = new BasicModel.CharacterClass { Id = Guid.NewGuid(), Name = "Dark Wizard" };
        var knight = new BasicModel.CharacterClass { Id = Guid.NewGuid(), Name = "Dark Knight" };
        var configuration = new BasicModel.GameConfiguration();

        Skill NewSkill(short number)
        {
            var skill = new BasicModel.Skill { Id = Guid.NewGuid(), Number = number, Name = $"Skill {number}" };
            configuration.RawSkills.Add(skill);
            return skill;
        }

        void NewItem(byte group, short number, Skill skill, bool worn, byte maximumLevel, params CharacterClass[] classes)
        {
            var item = new BasicModel.ItemDefinition { Id = Guid.NewGuid(), Group = group, Number = number, Skill = skill, MaximumItemLevel = maximumLevel };
            if (worn)
            {
                item.ItemSlot = new BasicModel.ItemSlotType { Id = Guid.NewGuid() };
            }

            foreach (var characterClass in classes)
            {
                item.QualifiedCharacters.Add(characterClass);
            }

            configuration.RawItems.Add(item);
        }

        var fireBall = NewSkill(1);
        var twister = NewSkill(8);
        var slash = NewSkill(19);
        var summonGoblin = NewSkill(30);
        var summonGolem = NewSkill(31);
        var master = NewSkill(400);
        master.MasterDefinition = new BasicModel.MasterSkillDefinition { Id = Guid.NewGuid() };
        NewItem(15, 2, fireBall, false, 0, wizard);
        NewItem(15, 3, twister, false, 0, wizard);
        NewItem(15, 4, twister, false, 0, wizard); // two scrolls of one skill: once
        NewItem(0, 1, slash, true, 15, wizard, knight); // a sword lends Slash while worn
        NewItem(12, 11, summonGoblin, false, 1, wizard); // the Summoning Orb: one skill per item level
        NewItem(15, 30, master, false, 0, wizard);

        Assert.Multiple(() =>
        {
            Assert.That(
                MUIdleClassSkills.LearnableBy(wizard, configuration).Select(s => s.Number),
                Is.EqualTo(new short[] { fireBall.Number, twister.Number, summonGoblin.Number, summonGolem.Number }));
            Assert.That(MUIdleClassSkills.LearnableBy(knight, configuration), Is.Empty);
        });
    }
}
