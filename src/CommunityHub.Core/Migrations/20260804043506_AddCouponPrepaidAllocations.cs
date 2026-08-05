using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCouponPrepaidAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CouponPrepaidAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CouponInvoicingSettingId = table.Column<int>(type: "int", nullable: false),
                    TicketClassId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TicketClassLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    QuantityPurchased = table.Column<int>(type: "int", nullable: false),
                    LowBalanceThreshold = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastLowBalanceAlertAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponPrepaidAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CouponPrepaidAllocations_CouponInvoicingSettings_CouponInvoicingSettingId",
                        column: x => x.CouponInvoicingSettingId,
                        principalTable: "CouponInvoicingSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CouponPrepaidAllocations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CouponPrepaidAllocations_CouponInvoicingSettingId_TicketClassId",
                table: "CouponPrepaidAllocations",
                columns: new[] { "CouponInvoicingSettingId", "TicketClassId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CouponPrepaidAllocations_EventId",
                table: "CouponPrepaidAllocations",
                column: "EventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CouponPrepaidAllocations");
        }
    }
}
