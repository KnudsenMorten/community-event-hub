using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionEvaluationSpeakers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvaluationSessionSpeakers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvaluationSessionId = table.Column<int>(type: "int", nullable: false),
                    SpeakerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CehParticipantId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationSessionSpeakers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationSessionSpeakers_EvaluationSessions_EvaluationSessionId",
                        column: x => x.EvaluationSessionId,
                        principalTable: "EvaluationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSessionSpeakers_EvaluationSessionId_SpeakerEmail",
                table: "EvaluationSessionSpeakers",
                columns: new[] { "EvaluationSessionId", "SpeakerEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSessionSpeakers_SpeakerEmail",
                table: "EvaluationSessionSpeakers",
                column: "SpeakerEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvaluationSessionSpeakers");
        }
    }
}
