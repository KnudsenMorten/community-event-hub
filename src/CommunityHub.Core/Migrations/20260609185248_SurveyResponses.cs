using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SurveyResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SurveyResponses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SurveySlug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SelectedTrackId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyResponses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SurveyResponsePicks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SurveyResponseId = table.Column<int>(type: "int", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    TopicId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DesiredLevel = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyResponsePicks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SurveyResponsePicks_SurveyResponses_SurveyResponseId",
                        column: x => x.SurveyResponseId,
                        principalTable: "SurveyResponses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponsePicks_SurveyResponseId_Rank",
                table: "SurveyResponsePicks",
                columns: new[] { "SurveyResponseId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponsePicks_SurveyResponseId_TopicId",
                table: "SurveyResponsePicks",
                columns: new[] { "SurveyResponseId", "TopicId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponsePicks_TopicId",
                table: "SurveyResponsePicks",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponses_SurveySlug_SelectedTrackId",
                table: "SurveyResponses",
                columns: new[] { "SurveySlug", "SelectedTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponses_SurveySlug_SubmittedAt",
                table: "SurveyResponses",
                columns: new[] { "SurveySlug", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SurveyResponsePicks");

            migrationBuilder.DropTable(
                name: "SurveyResponses");
        }
    }
}
