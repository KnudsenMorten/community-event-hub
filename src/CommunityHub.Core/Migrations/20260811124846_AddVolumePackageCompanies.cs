using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddVolumePackageCompanies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VolumePackageCompanies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CustomName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Domains = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CouponCodes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    LinkedEmails = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ErpCustomerNumbers = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ApprovedKeynoteMention = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedSocialMediaAnnouncement = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedGroupPhoto = table.Column<bool>(type: "bit", nullable: false),
                    DeclinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApproverEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ApproverName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApproverMobile = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    GroupPhotoContactEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    GroupPhotoContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GroupPhotoContactMobile = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LinkedInUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LogoWebPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LogoPrintPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    QualifiedNow = table.Column<bool>(type: "bit", nullable: false),
                    LastQualifiedCount = table.Column<int>(type: "int", nullable: false),
                    FirstQualifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BenefitsApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BenefitsApprovedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumePackageCompanies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolumePackageCompanies_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolumePackageQualificationSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    VolumePackageCompanyId = table.Column<int>(type: "int", nullable: false),
                    OnDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AttendeeCount = table.Column<int>(type: "int", nullable: false),
                    Qualified = table.Column<bool>(type: "bit", nullable: false),
                    FromOrders = table.Column<int>(type: "int", nullable: false),
                    FromCoupons = table.Column<int>(type: "int", nullable: false),
                    FromAttendeeDomains = table.Column<int>(type: "int", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumePackageQualificationSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolumePackageQualificationSnapshots_VolumePackageCompanies_VolumePackageCompanyId",
                        column: x => x.VolumePackageCompanyId,
                        principalTable: "VolumePackageCompanies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolumePackageCompanies_EventId_CustomName",
                table: "VolumePackageCompanies",
                columns: new[] { "EventId", "CustomName" });

            migrationBuilder.CreateIndex(
                name: "IX_VolumePackageQualificationSnapshots_VolumePackageCompanyId_OnDate",
                table: "VolumePackageQualificationSnapshots",
                columns: new[] { "VolumePackageCompanyId", "OnDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VolumePackageQualificationSnapshots");

            migrationBuilder.DropTable(
                name: "VolumePackageCompanies");
        }
    }
}
