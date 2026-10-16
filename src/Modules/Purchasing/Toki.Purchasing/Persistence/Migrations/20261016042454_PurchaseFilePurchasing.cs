using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Purchasing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseFilePurchasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttachmentName",
                schema: "purchasing",
                table: "Purchases",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PurchaseFiles",
                schema: "purchasing",
                columns: table => new
                {
                    PurchaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseFiles", x => x.PurchaseId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseFiles",
                schema: "purchasing");

            migrationBuilder.DropColumn(
                name: "AttachmentName",
                schema: "purchasing",
                table: "Purchases");
        }
    }
}
