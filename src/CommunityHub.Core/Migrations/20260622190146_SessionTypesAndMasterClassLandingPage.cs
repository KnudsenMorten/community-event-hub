using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionTypesAndMasterClassLandingPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FEATURE 1: standardize SessionType (stored as int). Remap the LEGACY
            // values to the new contract before anything else:
            //   old 0 CommunityMasterClass -> new 2 MasterClass
            //   old 1 CommunityTechSession -> new 1 TechnicalSession (unchanged)
            //   old 2 SponsorSession       -> new 1 TechnicalSession
            // Order: collapse SponsorSession (2 -> 1) FIRST so the master-class move
            // (0 -> 2) cannot collide with the still-old SponsorSession value 2.
            migrationBuilder.Sql("UPDATE [Sessions] SET [Type] = 1 WHERE [Type] = 2;");
            migrationBuilder.Sql("UPDATE [Sessions] SET [Type] = 2 WHERE [Type] = 0;");

            migrationBuilder.AddColumn<string>(
                name: "PrepContent",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PrepUpdatedAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrepUpdatedByParticipantId",
                table: "Sessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TypeIsManualOverride",
                table: "Sessions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AskerAttendeeId",
                table: "SessionQuestions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrivate",
                table: "SessionQuestions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MasterClassComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    AuthorParticipantId = table.Column<int>(type: "int", nullable: true),
                    AuthorAttendeeId = table.Column<int>(type: "int", nullable: true),
                    AuthorDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ParentCommentId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterClassComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterClassComments_Attendees_AuthorAttendeeId",
                        column: x => x.AuthorAttendeeId,
                        principalTable: "Attendees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassComments_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MasterClassComments_MasterClassComments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "MasterClassComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MasterClassComments_Participants_AuthorParticipantId",
                        column: x => x.AuthorParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassComments_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_AskerAttendeeId",
                table: "SessionQuestions",
                column: "AskerAttendeeId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_SessionId_AskerAttendeeId",
                table: "SessionQuestions",
                columns: new[] { "SessionId", "AskerAttendeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_AuthorAttendeeId",
                table: "MasterClassComments",
                column: "AuthorAttendeeId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_AuthorParticipantId",
                table: "MasterClassComments",
                column: "AuthorParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_EventId",
                table: "MasterClassComments",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_ParentCommentId",
                table: "MasterClassComments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_SessionId_CreatedAt",
                table: "MasterClassComments",
                columns: new[] { "SessionId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_SessionQuestions_Attendees_AskerAttendeeId",
                table: "SessionQuestions",
                column: "AskerAttendeeId",
                principalTable: "Attendees",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SessionQuestions_Attendees_AskerAttendeeId",
                table: "SessionQuestions");

            migrationBuilder.DropTable(
                name: "MasterClassComments");

            migrationBuilder.DropIndex(
                name: "IX_SessionQuestions_AskerAttendeeId",
                table: "SessionQuestions");

            migrationBuilder.DropIndex(
                name: "IX_SessionQuestions_SessionId_AskerAttendeeId",
                table: "SessionQuestions");

            migrationBuilder.DropColumn(
                name: "PrepContent",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PrepUpdatedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PrepUpdatedByParticipantId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "TypeIsManualOverride",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "AskerAttendeeId",
                table: "SessionQuestions");

            migrationBuilder.DropColumn(
                name: "IsPrivate",
                table: "SessionQuestions");

            // Best-effort reverse of the FEATURE 1 type remap: move MasterClass back to
            // the legacy CommunityMasterClass value (2 -> 0). The SponsorSession split
            // (old 2 -> 1) is NOT recoverable here (old 1 and old 2 both became 1), so
            // it is intentionally left as TechnicalSession.
            migrationBuilder.Sql("UPDATE [Sessions] SET [Type] = 0 WHERE [Type] = 2;");
        }
    }
}
