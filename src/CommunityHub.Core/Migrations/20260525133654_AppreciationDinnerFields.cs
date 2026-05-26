using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AppreciationDinnerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CalendarInviteSentAt",
                table: "DinnerSignups",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Comments",
                table: "DinnerSignups",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlusOneCount",
                table: "DinnerSignups",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Rsvp",
                table: "DinnerSignups",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarInviteSentAt",
                table: "DinnerSignups");

            migrationBuilder.DropColumn(
                name: "Comments",
                table: "DinnerSignups");

            migrationBuilder.DropColumn(
                name: "PlusOneCount",
                table: "DinnerSignups");

            migrationBuilder.DropColumn(
                name: "Rsvp",
                table: "DinnerSignups");
        }
    }
}
