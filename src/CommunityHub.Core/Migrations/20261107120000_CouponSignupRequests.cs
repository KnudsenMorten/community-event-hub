using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>§1382 — the public coupon sign-up form's request table. Additive only.</summary>
    public partial class CouponSignupRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CouponSignupRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OrderType = table.Column<int>(type: "int", nullable: false),
                    TicketType = table.Column<int>(type: "int", nullable: false),
                    TicketCount = table.Column<int>(type: "int", nullable: false),
                    VatNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CountryIso2 = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    RequesterName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequesterEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    RequesterMobile = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    InvoiceEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    EanNumber = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: true),
                    SplitPercent = table.Column<int>(type: "int", nullable: false),
                    PurchaseOrder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ErpCustomerNumber = table.Column<int>(type: "int", nullable: true),
                    ErpCustomerCreated = table.Column<bool>(type: "bit", nullable: false),
                    ErpError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CouponInvoicingSettingId = table.Column<int>(type: "int", nullable: true),
                    MailSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RequestIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponSignupRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CouponSignupRequests_EventId_CreatedAt",
                table: "CouponSignupRequests",
                columns: new[] { "EventId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CouponSignupRequests");
        }
    }
}
