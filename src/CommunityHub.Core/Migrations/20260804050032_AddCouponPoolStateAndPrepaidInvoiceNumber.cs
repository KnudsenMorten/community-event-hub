using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCouponPoolStateAndPrepaidInvoiceNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                table: "CouponPrepaidAllocations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedByEmail",
                table: "CouponPrepaidAllocations",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedReason",
                table: "CouponPrepaidAllocations",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ErpInvoiceConfirmedAt",
                table: "CouponPrepaidAllocations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpInvoiceConfirmedByEmail",
                table: "CouponPrepaidAllocations",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpInvoiceNumber",
                table: "CouponPrepaidAllocations",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastBillingReminderAt",
                table: "CouponPrepaidAllocations",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "ClosedByEmail",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "ClosedReason",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "ErpInvoiceConfirmedAt",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "ErpInvoiceConfirmedByEmail",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "ErpInvoiceNumber",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "LastBillingReminderAt",
                table: "CouponPrepaidAllocations");
        }
    }
}
