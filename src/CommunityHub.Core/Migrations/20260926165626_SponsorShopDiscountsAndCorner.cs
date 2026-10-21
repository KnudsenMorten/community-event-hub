using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorShopDiscountsAndCorner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PromotedSlot",
                table: "ShopProducts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SalePriceEur",
                table: "ShopProducts",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NormalUnitPriceEur",
                table: "ShopOrderLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromotedSlot",
                table: "ShopProducts");

            migrationBuilder.DropColumn(
                name: "SalePriceEur",
                table: "ShopProducts");

            migrationBuilder.DropColumn(
                name: "NormalUnitPriceEur",
                table: "ShopOrderLines");
        }
    }
}
