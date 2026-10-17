using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Settings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReferralTrialDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TrialDays",
                schema: "settings",
                table: "Business",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrialDays",
                schema: "settings",
                table: "Business");
        }
    }
}
