using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantFiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperDocuments_Ncf",
                schema: "fiscal",
                table: "PaperDocuments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Mode",
                schema: "fiscal",
                table: "Mode");

            migrationBuilder.DropIndex(
                name: "IX_EcfReceived_CloudId",
                schema: "fiscal",
                table: "EcfReceived");

            migrationBuilder.DropIndex(
                name: "IX_EcfDocuments_Environment_ENcf",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DgiiPublicAnswers",
                schema: "fiscal",
                table: "DgiiPublicAnswers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CertificationSteps",
                schema: "fiscal",
                table: "CertificationSteps");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "PaperSequences",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "PaperDocuments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "Mode",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfVoids",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfSequences",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfReceived",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfDeliveries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "DgiiPublicAnswers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "Contingencies",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "CertificationSteps",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "fiscal",
                table: "CertificationCases",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_Mode",
                schema: "fiscal",
                table: "Mode",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_DgiiPublicAnswers",
                schema: "fiscal",
                table: "DgiiPublicAnswers",
                columns: new[] { "TenantId", "Key" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_CertificationSteps",
                schema: "fiscal",
                table: "CertificationSteps",
                columns: new[] { "TenantId", "Step" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperDocuments_TenantId_Ncf",
                schema: "fiscal",
                table: "PaperDocuments",
                columns: new[] { "TenantId", "Ncf" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EcfReceived_TenantId_CloudId",
                schema: "fiscal",
                table: "EcfReceived",
                columns: new[] { "TenantId", "CloudId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EcfDocuments_TenantId_Environment_ENcf",
                schema: "fiscal",
                table: "EcfDocuments",
                columns: new[] { "TenantId", "Environment", "ENcf" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperDocuments_TenantId_Ncf",
                schema: "fiscal",
                table: "PaperDocuments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Mode",
                schema: "fiscal",
                table: "Mode");

            migrationBuilder.DropIndex(
                name: "IX_EcfReceived_TenantId_CloudId",
                schema: "fiscal",
                table: "EcfReceived");

            migrationBuilder.DropIndex(
                name: "IX_EcfDocuments_TenantId_Environment_ENcf",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DgiiPublicAnswers",
                schema: "fiscal",
                table: "DgiiPublicAnswers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CertificationSteps",
                schema: "fiscal",
                table: "CertificationSteps");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "PaperSequences");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "PaperDocuments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "Mode");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfVoids");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfSequences");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfReceived");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "EcfDeliveries");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "DgiiPublicAnswers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "Contingencies");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "CertificationSteps");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "fiscal",
                table: "CertificationCases");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Mode",
                schema: "fiscal",
                table: "Mode",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DgiiPublicAnswers",
                schema: "fiscal",
                table: "DgiiPublicAnswers",
                column: "Key");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CertificationSteps",
                schema: "fiscal",
                table: "CertificationSteps",
                column: "Step");

            migrationBuilder.CreateIndex(
                name: "IX_PaperDocuments_Ncf",
                schema: "fiscal",
                table: "PaperDocuments",
                column: "Ncf",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EcfReceived_CloudId",
                schema: "fiscal",
                table: "EcfReceived",
                column: "CloudId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EcfDocuments_Environment_ENcf",
                schema: "fiscal",
                table: "EcfDocuments",
                columns: new[] { "Environment", "ENcf" },
                unique: true);
        }
    }
}
