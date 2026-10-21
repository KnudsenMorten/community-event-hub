using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class EvaluationPhysicalRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PhysicalRoomId",
                table: "EvaluationDevices",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EvaluationPhysicalRooms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationPhysicalRooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationPhysicalRooms_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationRoomPlacements",
                columns: table => new
                {
                    PhysicalRoomId = table.Column<int>(type: "int", nullable: false),
                    EvaluationRoomId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationRoomPlacements", x => new { x.PhysicalRoomId, x.EvaluationRoomId });
                    table.ForeignKey(
                        name: "FK_EvaluationRoomPlacements_EvaluationPhysicalRooms_PhysicalRoomId",
                        column: x => x.PhysicalRoomId,
                        principalTable: "EvaluationPhysicalRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvaluationRoomPlacements_EvaluationRooms_EvaluationRoomId",
                        column: x => x.EvaluationRoomId,
                        principalTable: "EvaluationRooms",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_PhysicalRoomId",
                table: "EvaluationDevices",
                column: "PhysicalRoomId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationPhysicalRooms_EventId_Name",
                table: "EvaluationPhysicalRooms",
                columns: new[] { "EventId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationRoomPlacements_EvaluationRoomId",
                table: "EvaluationRoomPlacements",
                column: "EvaluationRoomId");

            migrationBuilder.AddForeignKey(
                name: "FK_EvaluationDevices_EvaluationPhysicalRooms_PhysicalRoomId",
                table: "EvaluationDevices",
                column: "PhysicalRoomId",
                principalTable: "EvaluationPhysicalRooms",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EvaluationDevices_EvaluationPhysicalRooms_PhysicalRoomId",
                table: "EvaluationDevices");

            migrationBuilder.DropTable(
                name: "EvaluationRoomPlacements");

            migrationBuilder.DropTable(
                name: "EvaluationPhysicalRooms");

            migrationBuilder.DropIndex(
                name: "IX_EvaluationDevices_PhysicalRoomId",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "PhysicalRoomId",
                table: "EvaluationDevices");
        }
    }
}
