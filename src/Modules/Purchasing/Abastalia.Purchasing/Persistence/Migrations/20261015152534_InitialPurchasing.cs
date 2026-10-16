using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Purchasing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPurchasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "purchasing");

            migrationBuilder.CreateTable(
                name: "Expenses",
                schema: "purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SupplierRnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    Ncf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Terms = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Subtotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    IsVoided = table.Column<bool>(type: "bit", nullable: false),
                    VoidReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Expenses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payables",
                schema: "purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ncf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    IsVoided = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payables", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Purchases",
                schema: "purchasing",
                columns: table => new
                {
                    Number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SupplierRnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    Ncf = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Terms = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PostedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Purchases", x => x.Number);
                    table.UniqueConstraint("AK_Purchases_Id", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPayments",
                schema: "purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ShiftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsVoided = table.Column<bool>(type: "bit", nullable: false),
                    VoidReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                schema: "purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Rnc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Contact = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreditDays = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseLines",
                schema: "purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(220)", maxLength: 220, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ItbisRate = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Itbis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseLines_Purchases_PurchaseId",
                        column: x => x.PurchaseId,
                        principalSchema: "purchasing",
                        principalTable: "Purchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAllocations",
                schema: "purchasing",
                columns: table => new
                {
                    SupplierPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayableId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAllocations", x => new { x.SupplierPaymentId, x.PayableId });
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_SupplierPayments_SupplierPaymentId",
                        column: x => x.SupplierPaymentId,
                        principalSchema: "purchasing",
                        principalTable: "SupplierPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_Date",
                schema: "purchasing",
                table: "Expenses",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_SupplierId_Ncf",
                schema: "purchasing",
                table: "Expenses",
                columns: new[] { "SupplierId", "Ncf" },
                unique: true,
                filter: "[IsVoided] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Payables_DocumentId",
                schema: "purchasing",
                table: "Payables",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payables_SupplierId",
                schema: "purchasing",
                table: "Payables",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_PayableId",
                schema: "purchasing",
                table: "PaymentAllocations",
                column: "PayableId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseLines_PurchaseId",
                schema: "purchasing",
                table: "PurchaseLines",
                column: "PurchaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_InvoiceDate",
                schema: "purchasing",
                table: "Purchases",
                column: "InvoiceDate");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_SupplierId_Ncf",
                schema: "purchasing",
                table: "Purchases",
                columns: new[] { "SupplierId", "Ncf" },
                unique: true,
                filter: "[Status] <> 'Voided'");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_SupplierId",
                schema: "purchasing",
                table: "SupplierPayments",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_Name",
                schema: "purchasing",
                table: "Suppliers",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Expenses",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "Payables",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "PaymentAllocations",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "PurchaseLines",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "Suppliers",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "SupplierPayments",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "Purchases",
                schema: "purchasing");
        }
    }
}
