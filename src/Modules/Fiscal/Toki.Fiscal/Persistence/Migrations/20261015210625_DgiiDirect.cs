using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Toki.Fiscal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DgiiDirect : Migration
    {
        /// <inheritdoc />
        // Ranges and documents from before TK-149 were all simulated (no store was certified), so they keep "Simulated".
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateOnly>(
                name: "ExpiresOn",
                schema: "fiscal",
                table: "EcfSequences",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");

            migrationBuilder.AddColumn<string>(
                name: "Environment",
                schema: "fiscal",
                table: "EcfSequences",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Simulated");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedUtc",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Ecf");

            migrationBuilder.AddColumn<bool>(
                name: "Contingency",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DgiiCode",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DgiiMessages",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Environment",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Simulated");

            migrationBuilder.AddColumn<string>(
                name: "ReplacedByENcf",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "nvarchar(13)",
                maxLength: 13,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacesENcf",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "nvarchar(13)",
                maxLength: 13,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResultUtc",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RfceXml",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "varbinary(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentUtc",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SequenceExpiresOn",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SequenceUsed",
                schema: "fiscal",
                table: "EcfDocuments",
                type: "bit",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EcfVoids",
                schema: "fiscal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    From = table.Column<long>(type: "bigint", nullable: false),
                    To = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SignedXml = table.Column<byte[]>(type: "varbinary(8000)", maxLength: 8000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResultUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EcfVoids", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EcfVoids",
                schema: "fiscal");

            migrationBuilder.DropColumn(
                name: "Environment",
                schema: "fiscal",
                table: "EcfSequences");

            migrationBuilder.DropColumn(
                name: "ArchivedUtc",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "Channel",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "Contingency",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "DgiiCode",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "DgiiMessages",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "Environment",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "ReplacedByENcf",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "ReplacesENcf",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "ResultUtc",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "RfceXml",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "SentUtc",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "SequenceExpiresOn",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.DropColumn(
                name: "SequenceUsed",
                schema: "fiscal",
                table: "EcfDocuments");

            migrationBuilder.AlterColumn<DateOnly>(
                name: "ExpiresOn",
                schema: "fiscal",
                table: "EcfSequences",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1),
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);
        }
    }
}
