// <copyright file="MUIdleExtensions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework.Extensions.ModelBuilder;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MUnique.OpenMU.Persistence.EntityFramework.Model;

/// <summary>
/// Extensions for the <see cref="EntityTypeBuilder{TEntity}"/> of the MUIdle entities.
/// </summary>
internal static class MUIdleExtensions
{
    /// <summary>
    /// Applies the settings for the <see cref="MUIdleCharacterState"/> entity.
    /// </summary>
    /// <param name="builder">The builder.</param>
    public static void Apply(this EntityTypeBuilder<MUIdleCharacterState> builder)
    {
        builder.HasIndex(state => state.CharacterId).IsUnique();
        builder.HasOne<Character>()
            .WithMany()
            .HasForeignKey(state => state.CharacterId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
