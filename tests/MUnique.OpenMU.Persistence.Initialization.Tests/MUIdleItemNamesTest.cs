// <copyright file="MUIdleItemNamesTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Globalization;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Tests for <see cref="MUIdleItemNamesPlugInSeason6"/> (D31): misspelled item names corrected.
/// </summary>
[TestFixture]
public class MUIdleItemNamesTest
{
    /// <summary>
    /// The Paewang boots get the set's name; the rest of the set and other items stay as they are.
    /// </summary>
    [Test]
    public void PhaewangBootsBecomePaewangBoots()
    {
        var boots = new ItemDefinition { Group = 11, Number = 51, Name = "Phaewang Boots" };
        var mask = new ItemDefinition { Group = 7, Number = 51, Name = "Paewang Mask" };
        var other = new ItemDefinition { Group = 11, Number = 50, Name = "Phaewang Boots" };

        var renamed = MUIdleItemNamesPlugInSeason6.Correct([boots, mask, other]);

        Assert.That(renamed, Is.EqualTo(1));
        Assert.That(boots.Name.ValueInNeutralLanguage, Is.EqualTo("Paewang Boots"));
        Assert.That(mask.Name.ValueInNeutralLanguage, Is.EqualTo("Paewang Mask"));
        Assert.That(other.Name.ValueInNeutralLanguage, Is.EqualTo("Phaewang Boots"));
    }

    /// <summary>
    /// A translation added to the name stays; applied twice, nothing more changes.
    /// </summary>
    [Test]
    public void TranslationsStayAndTheFixIsDoneOnce()
    {
        var portuguese = CultureInfo.GetCultureInfo("pt");
        var boots = new ItemDefinition { Group = 11, Number = 51, Name = "Phaewang Boots" };
        boots.Name = boots.Name.WithTranslation(portuguese, "Botas reais");

        Assert.That(MUIdleItemNamesPlugInSeason6.Correct([boots]), Is.EqualTo(1));
        Assert.That(MUIdleItemNamesPlugInSeason6.Correct([boots]), Is.EqualTo(0));
        Assert.That(boots.Name.ValueInNeutralLanguage, Is.EqualTo("Paewang Boots"));
        Assert.That(boots.Name.GetTranslation(portuguese, false), Is.EqualTo("Botas reais"));
    }
}
