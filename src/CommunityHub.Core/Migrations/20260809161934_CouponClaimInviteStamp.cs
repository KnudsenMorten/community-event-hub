using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class CouponClaimInviteStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimInviteSentAt",
                table: "CouponInvoicingSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimInviteSentToEmail",
                table: "CouponInvoicingSettings",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimInviteSentAt",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "ClaimInviteSentToEmail",
                table: "CouponInvoicingSettings");
        }
    }
}
