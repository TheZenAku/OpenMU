using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MUnique.OpenMU.Persistence.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddMUIdleCharacterState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MUIdleCharacterState",
                schema: "data",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    HuntActive = table.Column<bool>(type: "boolean", nullable: false),
                    OfflineSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OfflineStatisticsJson = table.Column<string>(type: "text", nullable: true),
                    SettingsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MUIdleCharacterState", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MUIdleCharacterState_Character_CharacterId",
                        column: x => x.CharacterId,
                        principalSchema: "data",
                        principalTable: "Character",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MUIdleCharacterState_CharacterId",
                schema: "data",
                table: "MUIdleCharacterState",
                column: "CharacterId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MUIdleCharacterState",
                schema: "data");
        }
    }
}
