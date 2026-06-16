using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicToken",
                table: "Sessions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SessionQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    AskerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AskerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    QuestionText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ResponseText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RespondedByParticipantId = table.Column<int>(type: "int", nullable: true),
                    RespondedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionQuestions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SessionQuestions_Participants_RespondedByParticipantId",
                        column: x => x.RespondedByParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SessionQuestions_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_PublicToken",
                table: "Sessions",
                column: "PublicToken",
                unique: true,
                filter: "[PublicToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_EventId_IpHash",
                table: "SessionQuestions",
                columns: new[] { "EventId", "IpHash" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_EventId_Status_CreatedAt",
                table: "SessionQuestions",
                columns: new[] { "EventId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_RespondedByParticipantId",
                table: "SessionQuestions",
                column: "RespondedByParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_SessionId_Status",
                table: "SessionQuestions",
                columns: new[] { "SessionId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionQuestions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_PublicToken",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PublicToken",
                table: "Sessions");
        }
    }
}
