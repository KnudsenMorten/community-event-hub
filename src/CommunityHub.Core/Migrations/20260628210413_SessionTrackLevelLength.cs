using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionTrackLevelLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LengthMinutes",
                table: "Sessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Level",
                table: "Sessions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LengthMinutes",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "Sessions");
        }
    }
}
