using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Customers.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "customers");

            migrationBuilder.CreateTable(
                name: "CreditBalanceMovements",
                schema: "customers",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditBalanceMovements", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPayments",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ShiftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsVoided = table.Column<bool>(type: "bit", nullable: false),
                    VoidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    TaxId = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FiscalCredit = table.Column<bool>(type: "bit", nullable: false),
                    PriceLevel = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CreditLimit = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OnAccountCharges",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Document = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    DateUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    IsVoided = table.Column<bool>(type: "bit", nullable: false),
                    VoidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaidAtVoid = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnAccountCharges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPaymentApplications",
                schema: "customers",
                columns: table => new
                {
                    CustomerPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChargeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPaymentApplications", x => new { x.CustomerPaymentId, x.ChargeId });
                    table.ForeignKey(
                        name: "FK_CustomerPaymentApplications_CustomerPayments_CustomerPaymentId",
                        column: x => x.CustomerPaymentId,
                        principalSchema: "customers",
                        principalTable: "CustomerPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditBalanceMovements_CustomerId",
                schema: "customers",
                table: "CreditBalanceMovements",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditBalanceMovements_ReferenceId",
                schema: "customers",
                table: "CreditBalanceMovements",
                column: "ReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentApplications_ChargeId",
                schema: "customers",
                table: "CustomerPaymentApplications",
                column: "ChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_CustomerId",
                schema: "customers",
                table: "CustomerPayments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Name",
                schema: "customers",
                table: "Customers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TaxId",
                schema: "customers",
                table: "Customers",
                column: "TaxId",
                unique: true,
                filter: "[TaxId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OnAccountCharges_CustomerId",
                schema: "customers",
                table: "OnAccountCharges",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_OnAccountCharges_SaleId",
                schema: "customers",
                table: "OnAccountCharges",
                column: "SaleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditBalanceMovements",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "CustomerPaymentApplications",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "Customers",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "OnAccountCharges",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "CustomerPayments",
                schema: "customers");
        }
    }
}
