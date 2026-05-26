using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class HotelConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CalendarInviteSentAt",
                table: "HotelBookings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmationNumber",
                table: "HotelBookings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConfirmationState",
                table: "HotelBookings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAt",
                table: "HotelBookings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoomType",
                table: "HotelBookings",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarInviteSentAt",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "ConfirmationNumber",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "ConfirmationState",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "RoomType",
                table: "HotelBookings");
        }
    }
}
