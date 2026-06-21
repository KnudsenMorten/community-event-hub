using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Section = table.Column<int>(type: "int", nullable: false),
                    OverrideJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConfigOverrides_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigOverrides_EventId_Section",
                table: "ConfigOverrides",
                columns: new[] { "EventId", "Section" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigOverrides");
        }
    }
}
