using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SoMePostMediaKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 🔴 defaultValue 2, NOT the scaffolder's 0. §842.7's knob defaults to 2 in the entity,
            // but EF scaffolds a raw 0 for an existing row — and the planner clamps 0 up to 1, which
            // would have silently HALVED the pacing of the live edition that already has a settings
            // row. An added column must land on the value the code means, not on the CLR default.
            migrationBuilder.AddColumn<int>(
                name: "MaxPostsPerDay",
                table: "SoMeSettings",
                type: "int",
                nullable: false,
                defaultValue: 2);

            // 0 = Graphic, which every pre-§844 post genuinely is. Correct as scaffolded.
            migrationBuilder.AddColumn<int>(
                name: "MediaKind",
                table: "SoMePosts",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxPostsPerDay",
                table: "SoMeSettings");

            migrationBuilder.DropColumn(
                name: "MediaKind",
                table: "SoMePosts");
        }
    }
}
