using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SignageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignageSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    PortraitToken = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LandscapeToken = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TokensRotatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PortraitNowEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PortraitNextEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PortraitFeedbackEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LandscapeNowEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LandscapeNextEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LandscapeFeedbackEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DailyFromLocal = table.Column<TimeOnly>(type: "time", nullable: true),
                    DailyToLocal = table.Column<TimeOnly>(type: "time", nullable: true),
                    ActiveFromLocal = table.Column<DateOnly>(type: "date", nullable: true),
                    ActiveToLocal = table.Column<DateOnly>(type: "date", nullable: true),
                    RotateSeconds = table.Column<int>(type: "int", nullable: false),
                    PageSeconds = table.Column<int>(type: "int", nullable: false),
                    PortraitColumns = table.Column<int>(type: "int", nullable: false),
                    PortraitRows = table.Column<int>(type: "int", nullable: false),
                    LandscapeColumns = table.Column<int>(type: "int", nullable: false),
                    LandscapeRows = table.Column<int>(type: "int", nullable: false),
                    PortraitGap = table.Column<int>(type: "int", nullable: false),
                    LandscapeGap = table.Column<int>(type: "int", nullable: false),
                    PortraitRowGap = table.Column<int>(type: "int", nullable: false),
                    LandscapeRowGap = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignageSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignageSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignageSettings_EventId",
                table: "SignageSettings",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignageSettings");
        }
    }
}
