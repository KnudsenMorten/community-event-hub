using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class KioskMasterClassRoom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MasterClassRoomId",
                table: "EvaluationDevices",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_MasterClassRoomId",
                table: "EvaluationDevices",
                column: "MasterClassRoomId");

            migrationBuilder.AddForeignKey(
                name: "FK_EvaluationDevices_EvaluationRooms_MasterClassRoomId",
                table: "EvaluationDevices",
                column: "MasterClassRoomId",
                principalTable: "EvaluationRooms",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EvaluationDevices_EvaluationRooms_MasterClassRoomId",
                table: "EvaluationDevices");

            migrationBuilder.DropIndex(
                name: "IX_EvaluationDevices_MasterClassRoomId",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "MasterClassRoomId",
                table: "EvaluationDevices");
        }
    }
}
