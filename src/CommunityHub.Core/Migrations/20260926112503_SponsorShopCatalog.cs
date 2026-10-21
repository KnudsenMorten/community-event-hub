using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorShopCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShopCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    WooCategoryId = table.Column<long>(type: "bigint", nullable: true),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    GroupSetByOrganizer = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopCategories_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShopCategories_ShopCategories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "ShopCategories",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ShopProducts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    WooProductId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ShortDescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PriceEur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ManageStock = table.Column<bool>(type: "bit", nullable: false),
                    StockQuantity = table.Column<int>(type: "int", nullable: true),
                    InStock = table.Column<bool>(type: "bit", nullable: false),
                    BoothId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Timeslot = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    TimeslotTime = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PrimaryCategoryId = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ContentManagedInCeh = table.Column<bool>(type: "bit", nullable: false),
                    WooModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopProducts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShopProducts_ShopCategories_PrimaryCategoryId",
                        column: x => x.PrimaryCategoryId,
                        principalTable: "ShopCategories",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ShopProductCategories",
                columns: table => new
                {
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopProductCategories", x => new { x.ProductId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_ShopProductCategories_ShopCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "ShopCategories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ShopProductCategories_ShopProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopProductImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AltText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SharePointPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SharePointItemId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CopyError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CopiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopProductImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopProductImages_ShopProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShopCategories_EventId_WooCategoryId",
                table: "ShopCategories",
                columns: new[] { "EventId", "WooCategoryId" },
                unique: true,
                filter: "[WooCategoryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopCategories_ParentId",
                table: "ShopCategories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductCategories_CategoryId",
                table: "ShopProductCategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductImages_ProductId",
                table: "ShopProductImages",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_EventId_BoothId",
                table: "ShopProducts",
                columns: new[] { "EventId", "BoothId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_EventId_WooProductId",
                table: "ShopProducts",
                columns: new[] { "EventId", "WooProductId" },
                unique: true,
                filter: "[WooProductId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_PrimaryCategoryId",
                table: "ShopProducts",
                column: "PrimaryCategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShopProductCategories");

            migrationBuilder.DropTable(
                name: "ShopProductImages");

            migrationBuilder.DropTable(
                name: "ShopProducts");

            migrationBuilder.DropTable(
                name: "ShopCategories");
        }
    }
}
