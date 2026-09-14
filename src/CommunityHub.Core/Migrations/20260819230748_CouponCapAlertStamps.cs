using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class CouponCapAlertStamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCapAlertAt",
                table: "CouponInvoicingSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastCapAlertRemaining",
                table: "CouponInvoicingSettings",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCapAlertAt",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "LastCapAlertRemaining",
                table: "CouponInvoicingSettings");
        }
    }
}
