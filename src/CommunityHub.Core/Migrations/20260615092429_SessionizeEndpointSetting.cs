using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionizeEndpointSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessionizeEndpointSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    EndpointId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    View = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    EndpointLastChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PreviousEndpointId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PendingChangeMode = table.Column<int>(type: "int", nullable: false),
                    ChangeModeChosenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionizeEndpointSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionizeEndpointSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionizeEndpointSettings_EventId",
                table: "SessionizeEndpointSettings",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionizeEndpointSettings");
        }
    }
}
