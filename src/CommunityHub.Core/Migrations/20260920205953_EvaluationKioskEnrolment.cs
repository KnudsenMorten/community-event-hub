using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class EvaluationKioskEnrolment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "KioskEnrolmentIssuedAt",
                table: "EvaluationDevices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KioskEnrolmentTokenHash",
                table: "EvaluationDevices",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KioskEnrolmentIssuedAt",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "KioskEnrolmentTokenHash",
                table: "EvaluationDevices");
        }
    }
}
