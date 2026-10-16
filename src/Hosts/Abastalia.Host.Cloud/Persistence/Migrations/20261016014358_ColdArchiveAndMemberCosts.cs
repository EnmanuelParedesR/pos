using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ColdArchiveAndMemberCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanViewCosts",
                schema: "cloud",
                table: "TenantMembers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "LinkShown",
                schema: "cloud",
                table: "TenantInvites",
                type: "bit",
                nullable: false,
                defaultValue: true); // R8 #1: invites from before had their link shown (no e-mail provider)

            migrationBuilder.AddColumn<bool>(
                name: "ColdHasRfce",
                schema: "cloud",
                table: "EcfRelays",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ColdKey",
                schema: "cloud",
                table: "EcfRelays",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ColdUtc",
                schema: "cloud",
                table: "EcfRelays",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EcfRelays_ColdUtc_ReceivedUtc",
                schema: "cloud",
                table: "EcfRelays",
                columns: new[] { "ColdUtc", "ReceivedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EcfRelays_ColdUtc_ReceivedUtc",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "CanViewCosts",
                schema: "cloud",
                table: "TenantMembers");

            migrationBuilder.DropColumn(
                name: "LinkShown",
                schema: "cloud",
                table: "TenantInvites");

            migrationBuilder.DropColumn(
                name: "ColdHasRfce",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "ColdKey",
                schema: "cloud",
                table: "EcfRelays");

            migrationBuilder.DropColumn(
                name: "ColdUtc",
                schema: "cloud",
                table: "EcfRelays");
        }
    }
}
