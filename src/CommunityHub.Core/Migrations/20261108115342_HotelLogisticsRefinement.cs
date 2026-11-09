using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class HotelLogisticsRefinement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgreementLabel",
                table: "Hotels",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgreementNumber",
                table: "Hotels",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingReference",
                table: "Hotels",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyEmails",
                table: "Hotels",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Hotels",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "WarnDaysBefore",
                table: "HotelCutoffs",
                type: "int",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<string>(
                name: "CancellationNumber",
                table: "HotelBookings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "HotelBookings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CancelledHotelId",
                table: "HotelBookings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalRooms",
                table: "HotelAllotments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HotelChangeLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    HotelId = table.Column<int>(type: "int", nullable: true),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    ParticipantName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AffectsGuestList = table.Column<bool>(type: "bit", nullable: false),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelChangeLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelChangeLog_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HotelForecastExtras",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Night = table.Column<DateOnly>(type: "date", nullable: false),
                    ExtraRooms = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelForecastExtras", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelForecastExtras_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HotelChangeLog_EventId_At",
                table: "HotelChangeLog",
                columns: new[] { "EventId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_HotelChangeLog_EventId_NotifiedAt",
                table: "HotelChangeLog",
                columns: new[] { "EventId", "NotifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HotelForecastExtras_EventId_Night",
                table: "HotelForecastExtras",
                columns: new[] { "EventId", "Night" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HotelChangeLog");

            migrationBuilder.DropTable(
                name: "HotelForecastExtras");

            migrationBuilder.DropColumn(
                name: "AgreementLabel",
                table: "Hotels");

            migrationBuilder.DropColumn(
                name: "AgreementNumber",
                table: "Hotels");

            migrationBuilder.DropColumn(
                name: "BookingReference",
                table: "Hotels");

            migrationBuilder.DropColumn(
                name: "NotifyEmails",
                table: "Hotels");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Hotels");

            migrationBuilder.DropColumn(
                name: "WarnDaysBefore",
                table: "HotelCutoffs");

            migrationBuilder.DropColumn(
                name: "CancellationNumber",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "CancelledHotelId",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "OriginalRooms",
                table: "HotelAllotments");
        }
    }
}
