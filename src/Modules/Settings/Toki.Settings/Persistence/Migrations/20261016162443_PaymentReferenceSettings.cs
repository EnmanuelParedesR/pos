using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Settings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentReferenceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequirePaymentReference",
                schema: "settings",
                table: "Business",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequirePaymentReference",
                schema: "settings",
                table: "Business");
        }
    }
}
