using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Sync.Persistence.Migrations
{
    /// <summary>TK-319: the old TenantId (the cloud tenant the store connected to) becomes CloudTenantId, keeping its value;
    /// TenantId is now the row's business, like every other table. Written by hand: EF would have reused the old column.</summary>
    public partial class TenantSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_CloudConnection",
                schema: "sync",
                table: "CloudConnection");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "sync",
                table: "CloudConnection",
                newName: "CloudTenantId");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "sync",
                table: "CloudConnection",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_CloudConnection",
                schema: "sync",
                table: "CloudConnection",
                columns: new[] { "TenantId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_CloudConnection",
                schema: "sync",
                table: "CloudConnection");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "sync",
                table: "CloudConnection");

            migrationBuilder.RenameColumn(
                name: "CloudTenantId",
                schema: "sync",
                table: "CloudConnection",
                newName: "TenantId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CloudConnection",
                schema: "sync",
                table: "CloudConnection",
                column: "Id");
        }
    }
}
