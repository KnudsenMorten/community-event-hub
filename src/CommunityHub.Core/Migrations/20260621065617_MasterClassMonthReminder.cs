using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class MasterClassMonthReminder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MonthReminderSentAt",
                table: "MasterClassSignups",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WantsMonthBeforeReminder",
                table: "MasterClassSignups",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonthReminderSentAt",
                table: "MasterClassSignups");

            migrationBuilder.DropColumn(
                name: "WantsMonthBeforeReminder",
                table: "MasterClassSignups");
        }
    }
}
