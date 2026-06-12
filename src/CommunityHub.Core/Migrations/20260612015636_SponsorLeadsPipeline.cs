using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorLeadsPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SponsorApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    KeyPrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IssuedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SponsorLeadNotificationPrefs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Cadence = table.Column<int>(type: "int", nullable: false),
                    SkipJunk = table.Column<bool>(type: "bit", nullable: false),
                    LastDeltaSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorLeadNotificationPrefs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SponsorLeads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ZohoRecordId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LeadKind = table.Column<int>(type: "int", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Company = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    JobTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    City = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusChangedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AiScreenScore = table.Column<int>(type: "int", nullable: true),
                    AiScreenLabel = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    AiScreenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastReplyAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastReplyByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorLeads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SponsorTokenVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    BumpedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BumpedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorTokenVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorApiKeys_EventId_SponsorCompanyId_RevokedAt",
                table: "SponsorApiKeys",
                columns: new[] { "EventId", "SponsorCompanyId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorLeadNotificationPrefs_EventId_SponsorCompanyId",
                table: "SponsorLeadNotificationPrefs",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorLeads_EventId_SponsorCompanyId_CapturedAt",
                table: "SponsorLeads",
                columns: new[] { "EventId", "SponsorCompanyId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorLeads_EventId_SponsorCompanyId_Status",
                table: "SponsorLeads",
                columns: new[] { "EventId", "SponsorCompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorLeads_EventId_ZohoRecordId",
                table: "SponsorLeads",
                columns: new[] { "EventId", "ZohoRecordId" },
                unique: true,
                filter: "[ZohoRecordId] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_SponsorTokenVersions_EventId_SponsorCompanyId",
                table: "SponsorTokenVersions",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SponsorApiKeys");

            migrationBuilder.DropTable(
                name: "SponsorLeadNotificationPrefs");

            migrationBuilder.DropTable(
                name: "SponsorLeads");

            migrationBuilder.DropTable(
                name: "SponsorTokenVersions");
        }
    }
}
