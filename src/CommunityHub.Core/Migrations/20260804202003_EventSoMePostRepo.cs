using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class EventSoMePostRepo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventSoMePosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    AllowImportOverwrite = table.Column<bool>(type: "bit", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastOverwrittenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SourceFileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSoMePosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventSoMePosts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventSoMePostOccurrences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventSoMePostId = table.Column<int>(type: "int", nullable: false),
                    PostDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    GraphicFileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SourcePhotoFileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSoMePostOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventSoMePostOccurrences_EventSoMePosts_EventSoMePostId",
                        column: x => x.EventSoMePostId,
                        principalTable: "EventSoMePosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventSoMePostOccurrences_EventSoMePostId_Sequence",
                table: "EventSoMePostOccurrences",
                columns: new[] { "EventSoMePostId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventSoMePosts_EventId_Slug",
                table: "EventSoMePosts",
                columns: new[] { "EventId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventSoMePostOccurrences");

            migrationBuilder.DropTable(
                name: "EventSoMePosts");
        }
    }
}
