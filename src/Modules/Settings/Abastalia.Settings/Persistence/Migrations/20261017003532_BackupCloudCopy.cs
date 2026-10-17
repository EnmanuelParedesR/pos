using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Settings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupCloudCopy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BackupCloud",
                schema: "settings",
                table: "Business",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupCloudFile",
                schema: "settings",
                table: "Business",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BackupCloudUtc",
                schema: "settings",
                table: "Business",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupCloud",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "BackupCloudFile",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "BackupCloudUtc",
                schema: "settings",
                table: "Business");
        }
    }
}
