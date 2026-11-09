using System;
using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §1386 — rooms, not people: <c>HotelBookings.GuestsInRoom</c> (organizer-set people in the room) and
    /// <c>HotelBookings.RoommateParticipantId</c> (two participants sharing one room); and the sent-to-hotel
    /// marker <c>Hotels.SentToHotelAt / SentToHotelByEmail</c>. Additive, all nullable.
    /// </summary>
    /// <remarks>Hand-written (no local build on the management host); the model snapshot carries the same four columns.</remarks>
    [DbContext(typeof(CommunityHubDbContext))]
    [Migration("20261108150000_HotelRoomsAndSentMarker")]
    public partial class HotelRoomsAndSentMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GuestsInRoom",
                table: "HotelBookings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoommateParticipantId",
                table: "HotelBookings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentToHotelAt",
                table: "Hotels",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SentToHotelByEmail",
                table: "Hotels",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "GuestsInRoom", table: "HotelBookings");
            migrationBuilder.DropColumn(name: "RoommateParticipantId", table: "HotelBookings");
            migrationBuilder.DropColumn(name: "SentToHotelAt", table: "Hotels");
            migrationBuilder.DropColumn(name: "SentToHotelByEmail", table: "Hotels");
        }
    }
}
