using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class ShopOrganizerReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ReservationDeadline",
                table: "ShopOrders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReservationNote",
                table: "ShopOrders",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReservationRemindFrom",
                table: "ShopOrders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservationReminderWeeks",
                table: "ShopOrders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReservationRequesterEmail",
                table: "ShopOrders",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReservationRequesterName",
                table: "ShopOrders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReservationDeadline",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ReservationNote",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ReservationRemindFrom",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ReservationReminderWeeks",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ReservationRequesterEmail",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ReservationRequesterName",
                table: "ShopOrders");
        }
    }
}
