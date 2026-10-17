using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Settings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Logo",
                schema: "settings",
                table: "Business",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LogoChangedUtc",
                schema: "settings",
                table: "Business",
                type: "datetime2",
                nullable: true);
            // No UpdateData of the seeded row: the new columns are already null, and a migration never rewrites the owner's row.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Logo",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "LogoChangedUtc",
                schema: "settings",
                table: "Business");
        }
    }
}
