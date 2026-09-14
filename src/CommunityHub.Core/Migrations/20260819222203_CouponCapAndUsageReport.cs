using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class CouponCapAndUsageReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClaimCapTickets",
                table: "CouponInvoicingSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCapRequestAt",
                table: "AttendeeMonitors",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastCapRequestedTickets",
                table: "AttendeeMonitors",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UsageReportSentAt",
                table: "AttendeeMonitors",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimCapTickets",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "LastCapRequestAt",
                table: "AttendeeMonitors");

            migrationBuilder.DropColumn(
                name: "LastCapRequestedTickets",
                table: "AttendeeMonitors");

            migrationBuilder.DropColumn(
                name: "UsageReportSentAt",
                table: "AttendeeMonitors");
        }
    }
}
