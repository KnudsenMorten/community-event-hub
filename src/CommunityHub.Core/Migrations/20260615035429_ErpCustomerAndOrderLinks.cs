using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class ErpCustomerAndOrderLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErpCustomerLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ErpCustomerNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Cvr = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CvrValid = table.Column<bool>(type: "bit", nullable: false),
                    CvrValidationReason = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CvrOfflineOnly = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpCustomerLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErpOrderLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WebshopOrderId = table.Column<long>(type: "bigint", nullable: false),
                    ErpOrderNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    FxRateApplied = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CurrencyCheckResult = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpOrderLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErpCustomerLinks_EventId_SponsorCompanyId",
                table: "ErpCustomerLinks",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpOrderLinks_EventId_WebshopOrderId",
                table: "ErpOrderLinks",
                columns: new[] { "EventId", "WebshopOrderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErpCustomerLinks");

            migrationBuilder.DropTable(
                name: "ErpOrderLinks");
        }
    }
}
