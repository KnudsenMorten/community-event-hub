using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class WebshopOrderArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebshopOrderArchives",
                columns: table => new
                {
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BillingCompany = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BillingEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebshopOrderArchives", x => x.OrderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebshopOrderArchives_EventId_CompanyId",
                table: "WebshopOrderArchives",
                columns: new[] { "EventId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_WebshopOrderArchives_EventId_Status",
                table: "WebshopOrderArchives",
                columns: new[] { "EventId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebshopOrderArchives");
        }
    }
}
