using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSponsorCategoryRounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "SponsorCategoryRound1From",
                table: "SoMeSettings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SponsorCategoryRound2From",
                table: "SoMeSettings",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SponsorCategoryRound1From",
                table: "SoMeSettings");

            migrationBuilder.DropColumn(
                name: "SponsorCategoryRound2From",
                table: "SoMeSettings");
        }
    }
}
