using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class WelcomeAutoLoginSingleUseGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MagicLinkGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TokenIdHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MagicLinkGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MagicLinkGrants_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MagicLinkGrants_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkGrants_EventId_ParticipantId_Purpose",
                table: "MagicLinkGrants",
                columns: new[] { "EventId", "ParticipantId", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkGrants_ParticipantId",
                table: "MagicLinkGrants",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkGrants_TokenIdHash",
                table: "MagicLinkGrants",
                column: "TokenIdHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MagicLinkGrants");
        }
    }
}
