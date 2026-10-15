using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Settings.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupervisorApprovalsSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SupervisorApprovals",
                schema: "settings",
                table: "Business",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
            // Null keeps the defaults (ApprovalPolicy.Defaults): every store asks for what it asked before.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupervisorApprovals",
                schema: "settings",
                table: "Business");
        }
    }
}
