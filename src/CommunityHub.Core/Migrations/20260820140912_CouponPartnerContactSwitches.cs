using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class CouponPartnerContactSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IssueUsageLink",
                table: "CouponInvoicingSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SendClaimInviteMail",
                table: "CouponInvoicingSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SendUsageStatusMail",
                table: "CouponInvoicingSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IssueUsageLink",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "SendClaimInviteMail",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "SendUsageStatusMail",
                table: "CouponInvoicingSettings");
        }
    }
}
