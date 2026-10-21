using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorShopInvoicingSwag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UsedAt",
                table: "SwagCatalogCredits",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsedOnShopOrderId",
                table: "SwagCatalogCredits",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ErpDraftInvoiceNumber",
                table: "ShopOrders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpInvoiceReference",
                table: "ShopOrders",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceProblem",
                table: "ShopOrders",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InvoicedAt",
                table: "ShopOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SwagCreditEur",
                table: "ShopOrders",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "PiecesPerUnit",
                table: "ShopOrderLines",
                type: "int",
                nullable: false,
                defaultValue: 1); // §1305: an existing line is one piece per unit

            migrationBuilder.AddColumn<string>(
                name: "VatCheck",
                table: "ShopCompanies",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UsedAt",
                table: "SwagCatalogCredits");

            migrationBuilder.DropColumn(
                name: "UsedOnShopOrderId",
                table: "SwagCatalogCredits");

            migrationBuilder.DropColumn(
                name: "ErpDraftInvoiceNumber",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "ErpInvoiceReference",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "InvoiceProblem",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "InvoicedAt",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "SwagCreditEur",
                table: "ShopOrders");

            migrationBuilder.DropColumn(
                name: "PiecesPerUnit",
                table: "ShopOrderLines");

            migrationBuilder.DropColumn(
                name: "VatCheck",
                table: "ShopCompanies");
        }
    }
}
