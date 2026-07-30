using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionEvalFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessionEvaluationFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    UploadedByParticipantId = table.Column<int>(type: "int", nullable: true),
                    UploadedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionEvaluationFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionEvaluationFiles_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SessionEvaluationFiles_Participants_UploadedByParticipantId",
                        column: x => x.UploadedByParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SessionEvaluationFiles_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvaluationFiles_EventId_SessionId",
                table: "SessionEvaluationFiles",
                columns: new[] { "EventId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvaluationFiles_SessionId_Kind",
                table: "SessionEvaluationFiles",
                columns: new[] { "SessionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvaluationFiles_UploadedByParticipantId",
                table: "SessionEvaluationFiles",
                column: "UploadedByParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionEvaluationFiles");
        }
    }
}
