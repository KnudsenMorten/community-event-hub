using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class GraphicsAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GraphicAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    StableKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    SessionId = table.Column<int>(type: "int", nullable: true),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SharePointPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SharePointUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StorageItemId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IsOrganizerOverridden = table.Column<bool>(type: "bit", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphicAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GraphicAssets_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GraphicAssets_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GraphicAssets_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GraphicsAssetLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    PersonaGroup = table.Column<int>(type: "int", nullable: false),
                    SiteUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DriveName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RootFolderPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    BrowseUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphicsAssetLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GraphicsAssetLocations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_EventId_ParticipantId_Status",
                table: "GraphicAssets",
                columns: new[] { "EventId", "ParticipantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_EventId_StableKey",
                table: "GraphicAssets",
                columns: new[] { "EventId", "StableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_EventId_Type_Status",
                table: "GraphicAssets",
                columns: new[] { "EventId", "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_ParticipantId",
                table: "GraphicAssets",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_SessionId",
                table: "GraphicAssets",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphicsAssetLocations_EventId_PersonaGroup",
                table: "GraphicsAssetLocations",
                columns: new[] { "EventId", "PersonaGroup" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GraphicAssets");

            migrationBuilder.DropTable(
                name: "GraphicsAssetLocations");
        }
    }
}
