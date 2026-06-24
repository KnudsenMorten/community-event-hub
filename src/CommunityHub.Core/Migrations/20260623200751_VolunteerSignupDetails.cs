using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VolunteerSignupDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgreementAcceptedAt",
                table: "VolunteerAvailabilities",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkedInUrl",
                table: "VolunteerAvailabilities",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoUrl",
                table: "VolunteerAvailabilities",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgreementAcceptedAt",
                table: "VolunteerAvailabilities");

            migrationBuilder.DropColumn(
                name: "LinkedInUrl",
                table: "VolunteerAvailabilities");

            migrationBuilder.DropColumn(
                name: "PhotoUrl",
                table: "VolunteerAvailabilities");
        }
    }
}
