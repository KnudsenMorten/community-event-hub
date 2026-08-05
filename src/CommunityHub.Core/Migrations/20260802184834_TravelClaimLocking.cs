using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class TravelClaimLocking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReopenCount",
                table: "TravelReimbursements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReopenedAt",
                table: "TravelReimbursements",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReopenedByEmail",
                table: "TravelReimbursements",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubmittedAt",
                table: "TravelReimbursements",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReopenCount",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "ReopenedAt",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "ReopenedByEmail",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "SubmittedAt",
                table: "TravelReimbursements");
        }
    }
}
