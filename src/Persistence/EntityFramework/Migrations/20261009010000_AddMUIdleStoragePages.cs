using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MUnique.OpenMU.Persistence.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddMUIdleStoragePages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StoragePages",
                schema: "data",
                table: "MUIdleCharacterState",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "StoragePagesId",
                schema: "data",
                table: "MUIdleCharacterState",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StoragePages",
                schema: "data",
                table: "MUIdleCharacterState");

            migrationBuilder.DropColumn(
                name: "StoragePagesId",
                schema: "data",
                table: "MUIdleCharacterState");
        }
    }
}
