using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SoMePlanningRuleConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxSponsorPostsPerDay",
                table: "SoMeSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinDaysBetweenRounds",
                table: "SoMeCategoryRules",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PublishBy",
                table: "SoMeCategoryRounds",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxSponsorPostsPerDay",
                table: "SoMeSettings");

            migrationBuilder.DropColumn(
                name: "MinDaysBetweenRounds",
                table: "SoMeCategoryRules");

            migrationBuilder.DropColumn(
                name: "PublishBy",
                table: "SoMeCategoryRounds");
        }
    }
}
