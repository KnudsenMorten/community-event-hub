using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class CouponBillingPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InvoiceIntervalDays",
                table: "CouponInvoicingSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastInvoicedAt",
                table: "CouponInvoicingSettings",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceIntervalDays",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "LastInvoicedAt",
                table: "CouponInvoicingSettings");
        }
    }
}
