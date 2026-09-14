using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class CouponAgreedPriceAndInvoicedShare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AgreedUnitPriceDkk",
                table: "CouponInvoicingSettings",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InvoicedSharePercent",
                table: "CouponInvoicingSettings",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgreedUnitPriceDkk",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "InvoicedSharePercent",
                table: "CouponInvoicingSettings");
        }
    }
}
