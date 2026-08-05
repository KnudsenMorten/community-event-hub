using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class LogisticsPageState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Headline",
                table: "LogisticsFileStates",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebUrl",
                table: "LogisticsFileStates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LogisticsRunSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    RanAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Published = table.Column<int>(type: "int", nullable: false),
                    Unchanged = table.Column<int>(type: "int", nullable: false),
                    Mailed = table.Column<int>(type: "int", nullable: false),
                    Problems = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Ok = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogisticsRunSummaries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LogisticsRunSummaries_EventId",
                table: "LogisticsRunSummaries",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LogisticsRunSummaries");

            migrationBuilder.DropColumn(
                name: "Headline",
                table: "LogisticsFileStates");

            migrationBuilder.DropColumn(
                name: "WebUrl",
                table: "LogisticsFileStates");
        }
    }
}
