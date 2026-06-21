using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class HotelRoomBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RoomBlockSize",
                table: "Hotels",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoomBlockSize",
                table: "Hotels");
        }
    }
}
