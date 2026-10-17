using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Customers.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NameCollationCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                schema: "customers",
                table: "Customers",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                collation: "Latin1_General_100_CI_AI",
                oldClrType: typeof(string),
                oldType: "nvarchar(150)",
                oldMaxLength: 150);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                schema: "customers",
                table: "Customers",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(150)",
                oldMaxLength: 150,
                oldCollation: "Latin1_General_100_CI_AI");
        }
    }
}
