using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PanelCommercialDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PanelDecidedBy",
                schema: "cloud",
                table: "FeReceived",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PanelDecidedUtc",
                schema: "cloud",
                table: "FeReceived",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PanelDecision",
                schema: "cloud",
                table: "FeReceived",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PanelReason",
                schema: "cloud",
                table: "FeReceived",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PanelDecidedBy",
                schema: "cloud",
                table: "FeReceived");

            migrationBuilder.DropColumn(
                name: "PanelDecidedUtc",
                schema: "cloud",
                table: "FeReceived");

            migrationBuilder.DropColumn(
                name: "PanelDecision",
                schema: "cloud",
                table: "FeReceived");

            migrationBuilder.DropColumn(
                name: "PanelReason",
                schema: "cloud",
                table: "FeReceived");
        }
    }
}
