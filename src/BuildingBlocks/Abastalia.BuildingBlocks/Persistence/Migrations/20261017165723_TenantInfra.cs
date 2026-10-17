using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.BuildingBlocks.Persistence.Migrations
{
    /// <summary>TK-319: TenantId on every table (the store's rows get the fixed store tenant); keys and unique indexes that two businesses could
    /// repeat lead with it. EF also wrote a delete and re-insert of the seed rows because their key changed: removed by hand, the store keeps
    /// what the owner edited.</summary>
    public partial class TenantInfra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AuditRecords",
                schema: "infra",
                table: "AuditRecords");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AuditHead",
                schema: "infra",
                table: "AuditHead");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "infra",
                table: "OutboxMessages",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "infra",
                table: "AuditRecords",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "infra",
                table: "AuditHead",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_AuditRecords",
                schema: "infra",
                table: "AuditRecords",
                columns: new[] { "TenantId", "Sequence" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AuditHead",
                schema: "infra",
                table: "AuditHead",
                columns: new[] { "TenantId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AuditRecords",
                schema: "infra",
                table: "AuditRecords");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AuditHead",
                schema: "infra",
                table: "AuditHead");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "infra",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "infra",
                table: "AuditRecords");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "infra",
                table: "AuditHead");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AuditRecords",
                schema: "infra",
                table: "AuditRecords",
                column: "Sequence");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AuditHead",
                schema: "infra",
                table: "AuditHead",
                column: "Id");
        }
    }
}
