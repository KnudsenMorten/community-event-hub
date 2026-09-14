using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSoMeTextEligibilityVerdict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SoMeTextEligible",
                table: "Sessions",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SoMeTextEligibleCheckedAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SoMeTextEligibleHash",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SoMeTextEligibleReason",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoMeTextEligible",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "SoMeTextEligibleCheckedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "SoMeTextEligibleHash",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "SoMeTextEligibleReason",
                table: "Sessions");
        }
    }
}
