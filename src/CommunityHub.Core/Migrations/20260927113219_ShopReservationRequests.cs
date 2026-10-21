using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class ShopReservationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtensionRequestReason",
                table: "ShopOrders",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExtensionRequestedAt",
                table: "ShopOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtensionRequestedByParticipantId",
                table: "ShopOrders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExtensionRequestedDeadline",
                table: "ShopOrders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReservationRequestedAt",
                table: "ShopOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservationRequestedByParticipantId",
                table: "ShopOrders",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtensionRequestReason",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ExtensionRequestedAt",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ExtensionRequestedByParticipantId",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ExtensionRequestedDeadline",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ReservationRequestedAt",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ReservationRequestedByParticipantId",
                table: "ShopOrders");
        }
    }
}
