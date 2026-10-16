using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Referrals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstPaidUtc",
                schema: "cloud",
                table: "TenantSubscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferralCode",
                schema: "cloud",
                table: "TenantSubscriptions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferredByCode",
                schema: "cloud",
                table: "TenantSubscriptions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Referrals",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferrerTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferredTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    TransactionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DiscountType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DiscountAmount = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    PaddleDiscountId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PaddleSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DiscountFromUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AppliedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevertedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referrals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantSubscriptions_ReferralCode",
                schema: "cloud",
                table: "TenantSubscriptions",
                column: "ReferralCode",
                unique: true,
                filter: "[ReferralCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferredTenantId",
                schema: "cloud",
                table: "Referrals",
                column: "ReferredTenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferrerTenantId_Status",
                schema: "cloud",
                table: "Referrals",
                columns: new[] { "ReferrerTenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_TransactionId",
                schema: "cloud",
                table: "Referrals",
                column: "TransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Referrals",
                schema: "cloud");

            migrationBuilder.DropIndex(
                name: "IX_TenantSubscriptions_ReferralCode",
                schema: "cloud",
                table: "TenantSubscriptions");

            migrationBuilder.DropColumn(
                name: "FirstPaidUtc",
                schema: "cloud",
                table: "TenantSubscriptions");

            migrationBuilder.DropColumn(
                name: "ReferralCode",
                schema: "cloud",
                table: "TenantSubscriptions");

            migrationBuilder.DropColumn(
                name: "ReferredByCode",
                schema: "cloud",
                table: "TenantSubscriptions");
        }
    }
}
