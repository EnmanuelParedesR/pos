using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Settings.Persistence.Migrations
{
    /// <summary>TK-319: TenantId on every table (the store's rows get the fixed store tenant); keys and unique indexes that two businesses could
    /// repeat lead with it. EF also wrote a delete and re-insert of the seed rows because their key changed: removed by hand, the store keeps
    /// what the owner edited.</summary>
    public partial class TenantSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Business",
                schema: "settings",
                table: "Business");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "settings",
                table: "UserPreferences",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "settings",
                table: "RegisterVersions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "settings",
                table: "Business",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_Business",
                schema: "settings",
                table: "Business",
                columns: new[] { "TenantId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Business",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "settings",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "settings",
                table: "RegisterVersions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "settings",
                table: "Business");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Business",
                schema: "settings",
                table: "Business",
                column: "Id");
        }
    }
}
