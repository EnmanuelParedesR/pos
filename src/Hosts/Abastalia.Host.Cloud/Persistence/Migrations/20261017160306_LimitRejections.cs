using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Abastalia.Host.Cloud.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LimitRejections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LimitRejections",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MinuteUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Network = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IpKey = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    RetryAfter = table.Column<int>(type: "int", nullable: false),
                    Stores = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LimitRejections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LimitRejections_MinuteUtc",
                schema: "cloud",
                table: "LimitRejections",
                column: "MinuteUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LimitRejections",
                schema: "cloud");
        }
    }
}
