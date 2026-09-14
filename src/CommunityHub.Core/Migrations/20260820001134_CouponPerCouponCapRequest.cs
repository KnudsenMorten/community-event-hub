using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class CouponPerCouponCapRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequestedCapAt",
                table: "CouponInvoicingSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestedCapTickets",
                table: "CouponInvoicingSettings",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestedCapAt",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "RequestedCapTickets",
                table: "CouponInvoicingSettings");
        }
    }
}
