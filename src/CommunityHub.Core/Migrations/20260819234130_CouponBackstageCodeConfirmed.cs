using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class CouponBackstageCodeConfirmed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BackstageCodeConfirmedAt",
                table: "CouponInvoicingSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackstageCodeConfirmedByEmail",
                table: "CouponInvoicingSettings",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackstageCodeConfirmedAt",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "BackstageCodeConfirmedByEmail",
                table: "CouponInvoicingSettings");
        }
    }
}
