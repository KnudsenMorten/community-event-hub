using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SoMePostSubjectAndTemplateKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Occurrence",
                table: "SoMePosts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectKey",
                table: "SoMePosts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TemplateKind",
                table: "SoMePosts",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoMePosts_EventId_SubjectKey_Occurrence",
                table: "SoMePosts",
                columns: new[] { "EventId", "SubjectKey", "Occurrence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SoMePosts_EventId_SubjectKey_Occurrence",
                table: "SoMePosts");

            migrationBuilder.DropColumn(
                name: "Occurrence",
                table: "SoMePosts");

            migrationBuilder.DropColumn(
                name: "SubjectKey",
                table: "SoMePosts");

            migrationBuilder.DropColumn(
                name: "TemplateKind",
                table: "SoMePosts");
        }
    }
}
