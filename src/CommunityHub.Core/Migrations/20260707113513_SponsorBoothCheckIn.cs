using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorBoothCheckIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BoothCheckInSetAt",
                table: "SponsorInfos",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BoothCheckInSetByEmail",
                table: "SponsorInfos",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BoothCheckInSlot",
                table: "SponsorInfos",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoothCheckInSetAt",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "BoothCheckInSetByEmail",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "BoothCheckInSlot",
                table: "SponsorInfos");
        }
    }
}
