using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class OrderMirror : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "OrderId",
                table: "Attendees",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "Attendees",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MirrorState",
                table: "Attendees",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    BackstageOrderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BuyerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BuyerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CountryCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    City = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Postcode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    TaxId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OrderStatus = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SourceCreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RawJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MirrorState = table.Column<int>(type: "int", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.UniqueConstraint("AK_Orders_EventId_BackstageOrderId", x => new { x.EventId, x.BackstageOrderId });
                    table.ForeignKey(
                        name: "FK_Orders_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_MirrorState",
                table: "Attendees",
                columns: new[] { "EventId", "MirrorState" });

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_OrderId",
                table: "Attendees",
                columns: new[] { "EventId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_EventId_MirrorState",
                table: "Orders",
                columns: new[] { "EventId", "MirrorState" });

            // Backfill stub Orders from any pre-existing Attendee rows so the new
            // (EventId, OrderId) -> Orders(EventId, BackstageOrderId) FK validates
            // against existing data. Before the mirror there was no Orders table, so
            // attendees synced earlier carry an OrderId that points at nothing yet;
            // creating the FK against an empty Orders table would fail. We seed one
            // Active stub per (EventId, OrderId); the next authoritative sync upserts
            // the full order detail (buyer/company/status/raw) onto these rows.
            migrationBuilder.Sql(@"
INSERT INTO Orders (EventId, BackstageOrderId, MirrorState, LastSyncedAt, CreatedAt)
SELECT a.EventId, a.OrderId, 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
FROM Attendees a
WHERE a.OrderId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM Orders o WHERE o.EventId = a.EventId AND o.BackstageOrderId = a.OrderId)
GROUP BY a.EventId, a.OrderId;");

            migrationBuilder.AddForeignKey(
                name: "FK_Attendees_Orders_EventId_OrderId",
                table: "Attendees",
                columns: new[] { "EventId", "OrderId" },
                principalTable: "Orders",
                principalColumns: new[] { "EventId", "BackstageOrderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Attendees_Orders_EventId_OrderId",
                table: "Attendees");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Attendees_EventId_MirrorState",
                table: "Attendees");

            migrationBuilder.DropIndex(
                name: "IX_Attendees_EventId_OrderId",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "MirrorState",
                table: "Attendees");

            migrationBuilder.AlterColumn<string>(
                name: "OrderId",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }
    }
}
