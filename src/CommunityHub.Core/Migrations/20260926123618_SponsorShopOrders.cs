using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorShopOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShopBoothSpots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    BoothId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    XPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    YPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    WidthPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    HeightPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopBoothSpots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShopCatalogPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Chapter = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    BodyMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MediaUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProductGroup = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ShowBoothMap = table.Column<bool>(type: "bit", nullable: false),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopCatalogPages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShopCompanies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PublicName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Cvr = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Postcode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CountryIso2 = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    CompanyEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InvoiceEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LinkedInUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TwitterUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    VatZone = table.Column<int>(type: "int", nullable: false),
                    ErpCustomerNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ErpError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RegisteredByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    BecameSponsorAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopCompanies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopCompanies_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopEntitlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceEdition = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BoothId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Timeslot = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    MatchedProductId = table.Column<int>(type: "int", nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClaimedOrderLineId = table.Column<int>(type: "int", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeclinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopEntitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopEntitlements_ShopProducts_MatchedProductId",
                        column: x => x.MatchedProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ShopOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Number = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PurchaseOrderRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SignerParticipantId = table.Column<int>(type: "int", nullable: true),
                    CoordinatorParticipantId = table.Column<int>(type: "int", nullable: true),
                    SpecialTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    InvoiceNotBefore = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentDays = table.Column<int>(type: "int", nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    VatRatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentForSignatureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SignedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SignedByParticipantId = table.Column<int>(type: "int", nullable: true),
                    SignatureKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SignatureImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    SignedFromIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ContractPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ContractItemId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OnboardedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopOrders_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopRegistrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SessionTokenHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RequestIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopRegistrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShopOrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPriceEur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SurchargePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    StockTaken = table.Column<bool>(type: "bit", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopOrderLines_ShopOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ShopOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShopOrderLines_ShopProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShopBoothSpots_EventId_BoothId",
                table: "ShopBoothSpots",
                columns: new[] { "EventId", "BoothId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopCatalogPages_EventId_Slug",
                table: "ShopCatalogPages",
                columns: new[] { "EventId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopCompanies_EventId_Cvr",
                table: "ShopCompanies",
                columns: new[] { "EventId", "Cvr" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopCompanies_EventId_SponsorCompanyId",
                table: "ShopCompanies",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopEntitlements_EventId_SponsorCompanyId",
                table: "ShopEntitlements",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopEntitlements_MatchedProductId",
                table: "ShopEntitlements",
                column: "MatchedProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrderLines_OrderId",
                table: "ShopOrderLines",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrderLines_ProductId",
                table: "ShopOrderLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrders_EventId_Number",
                table: "ShopOrders",
                columns: new[] { "EventId", "Number" },
                unique: true,
                filter: "[Number] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrders_EventId_SponsorCompanyId_Status",
                table: "ShopOrders",
                columns: new[] { "EventId", "SponsorCompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopRegistrations_EventId_Email",
                table: "ShopRegistrations",
                columns: new[] { "EventId", "Email" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShopBoothSpots");

            migrationBuilder.DropTable(
                name: "ShopCatalogPages");

            migrationBuilder.DropTable(
                name: "ShopCompanies");

            migrationBuilder.DropTable(
                name: "ShopEntitlements");

            migrationBuilder.DropTable(
                name: "ShopOrderLines");

            migrationBuilder.DropTable(
                name: "ShopRegistrations");

            migrationBuilder.DropTable(
                name: "ShopOrders");
        }
    }
}
