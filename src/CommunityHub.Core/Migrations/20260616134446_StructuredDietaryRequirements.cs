using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class StructuredDietaryRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DietaryRequirements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Surface = table.Column<int>(type: "int", nullable: false),
                    DietChoice = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Gluten = table.Column<bool>(type: "bit", nullable: false),
                    Crustaceans = table.Column<bool>(type: "bit", nullable: false),
                    Eggs = table.Column<bool>(type: "bit", nullable: false),
                    Fish = table.Column<bool>(type: "bit", nullable: false),
                    Peanuts = table.Column<bool>(type: "bit", nullable: false),
                    Soybeans = table.Column<bool>(type: "bit", nullable: false),
                    Milk = table.Column<bool>(type: "bit", nullable: false),
                    TreeNuts = table.Column<bool>(type: "bit", nullable: false),
                    Celery = table.Column<bool>(type: "bit", nullable: false),
                    Mustard = table.Column<bool>(type: "bit", nullable: false),
                    Sesame = table.Column<bool>(type: "bit", nullable: false),
                    Sulphites = table.Column<bool>(type: "bit", nullable: false),
                    Lupin = table.Column<bool>(type: "bit", nullable: false),
                    Molluscs = table.Column<bool>(type: "bit", nullable: false),
                    OtherAllergens = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DietaryRequirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DietaryRequirements_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DietaryRequirements_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DietaryRequirements_EventId_ParticipantId_Surface",
                table: "DietaryRequirements",
                columns: new[] { "EventId", "ParticipantId", "Surface" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DietaryRequirements_EventId_Surface",
                table: "DietaryRequirements",
                columns: new[] { "EventId", "Surface" });

            migrationBuilder.CreateIndex(
                name: "IX_DietaryRequirements_ParticipantId",
                table: "DietaryRequirements",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DietaryRequirements");
        }
    }
}
