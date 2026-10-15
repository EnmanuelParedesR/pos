using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Settings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "settings");

            migrationBuilder.CreateTable(
                name: "Business",
                schema: "settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Rnc = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReceiptFooter = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DiscountCapPercent = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    BackupSalt = table.Column<byte[]>(type: "varbinary(16)", maxLength: 16, nullable: true),
                    BackupKey = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Business", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "settings",
                table: "Business",
                columns: new[] { "Id", "Address", "BackupKey", "BackupSalt", "DiscountCapPercent", "Name", "Phone", "ReceiptFooter", "Rnc" },
                values: new object[] { 1, null, null, null, 10m, "Mi tienda", null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Business",
                schema: "settings");
        }
    }
}
