using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoreBackups",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BlobKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastDownloadedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastDownloadedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreBackups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoreBackups_TenantId_StoreId_FileName",
                schema: "cloud",
                table: "StoreBackups",
                columns: new[] { "TenantId", "StoreId", "FileName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoreBackups",
                schema: "cloud");
        }
    }
}
