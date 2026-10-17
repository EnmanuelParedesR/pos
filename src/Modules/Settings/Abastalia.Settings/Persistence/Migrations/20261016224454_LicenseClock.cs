using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Settings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LicenseClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClockSeenSeal",
                schema: "settings",
                table: "Business",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClockSeenUtc",
                schema: "settings",
                table: "Business",
                type: "datetime2",
                nullable: true);

            migrationBuilder.UpdateData(
                schema: "settings",
                table: "Business",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ClockSeenSeal", "ClockSeenUtc" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClockSeenSeal",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "ClockSeenUtc",
                schema: "settings",
                table: "Business");
        }
    }
}
