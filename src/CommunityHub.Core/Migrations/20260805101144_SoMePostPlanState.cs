using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SoMePostPlanState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ defaultValue 0 = Proposed, and here that is CORRECT — unlike the two columns before
            // it, where the scaffolder's 0 was wrong (SoMePostMediaKind, SoMeExceptionPostsPerDay).
            // Every existing post is an un-accepted proposal: nothing on PROD has been approved,
            // edited or published. Checked rather than assumed, because 0 has been the wrong answer
            // twice already tonight.
            migrationBuilder.AddColumn<int>(
                name: "PlanState",
                table: "SoMePosts",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlanState",
                table: "SoMePosts");
        }
    }
}
