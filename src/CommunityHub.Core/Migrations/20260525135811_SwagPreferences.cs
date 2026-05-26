using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SwagPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SwagPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    WantsPolo = table.Column<bool>(type: "bit", nullable: false),
                    PoloSize = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    WantsJacket = table.Column<bool>(type: "bit", nullable: false),
                    JacketSize = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    WantsGift = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwagPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SwagPreferences_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SwagPreferences_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SwagPreferences_EventId_ParticipantId",
                table: "SwagPreferences",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SwagPreferences_ParticipantId",
                table: "SwagPreferences",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SwagPreferences");
        }
    }
}
