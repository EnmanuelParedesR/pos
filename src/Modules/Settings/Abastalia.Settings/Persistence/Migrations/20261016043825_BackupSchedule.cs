using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Settings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BackupAutomatic",
                schema: "settings",
                table: "Business",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupCopyTo",
                schema: "settings",
                table: "Business",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupKeep",
                schema: "settings",
                table: "Business",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "BackupTime",
                schema: "settings",
                table: "Business",
                type: "time",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupAutomatic",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "BackupCopyTo",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "BackupKeep",
                schema: "settings",
                table: "Business");

            migrationBuilder.DropColumn(
                name: "BackupTime",
                schema: "settings",
                table: "Business");
        }
    }
}
