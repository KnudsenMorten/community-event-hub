using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SurveyState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SurveyStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SurveySlug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyStates_SurveySlug",
                table: "SurveyStates",
                column: "SurveySlug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SurveyStates");
        }
    }
}
