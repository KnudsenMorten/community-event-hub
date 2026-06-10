using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorUploadTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SponsorUploadLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FolderKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Subfolder = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    FolderPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    EditLinkUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    NotifyEmailsCsv = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    NotifySubject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorUploadLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorUploadLocations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SponsorUploadFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SponsorUploadLocationId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    GraphItemId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ETag = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LastModifiedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastNotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorUploadFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorUploadFiles_SponsorUploadLocations_SponsorUploadLocationId",
                        column: x => x.SponsorUploadLocationId,
                        principalTable: "SponsorUploadLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorUploadFiles_SponsorUploadLocationId_GraphItemId",
                table: "SponsorUploadFiles",
                columns: new[] { "SponsorUploadLocationId", "GraphItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorUploadLocations_EventId_SponsorCompanyId_FolderKey",
                table: "SponsorUploadLocations",
                columns: new[] { "EventId", "SponsorCompanyId", "FolderKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SponsorUploadFiles");

            migrationBuilder.DropTable(
                name: "SponsorUploadLocations");
        }
    }
}
