using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EcfArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DgiiCode",
                schema: "cloud",
                table: "EcfRelays",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Environment",
                schema: "cloud",
                table: "EcfRelays",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReplacedByENcf",
                schema: "cloud",
                table: "EcfRelays",
                type: "nvarchar(13)",
                maxLength: 13,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReplacesENcf",
                schema: "cloud",
                table: "EcfRelays",
                type: "nvarchar(13)",
                maxLength: 13,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "RfceGzip",
                schema: "cloud",
                table: "EcfRelays",
                type: "varbinary(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedUtc",
                schema: "cloud",
                table: "EcfRelays",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedUtc",
                schema: "cloud",
                table: "EcfRelays",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DgiiCode",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "Environment",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "ReplacedByENcf",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "ReplacesENcf",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "RfceGzip",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "SignedUtc",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "UpdatedUtc",
                schema: "cloud",
                table: "EcfRelays");
        }
    }
}
