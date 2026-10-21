using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class EvaluationKioskPairing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsKiosk",
                table: "EvaluationDevices",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "KioskLastSeenAt",
                table: "EvaluationDevices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PairingCodeExpiresAt",
                table: "EvaluationDevices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PairingCodeHash",
                table: "EvaluationDevices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PairingCodeUsedAt",
                table: "EvaluationDevices",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsKiosk",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "KioskLastSeenAt",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "PairingCodeExpiresAt",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "PairingCodeHash",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "PairingCodeUsedAt",
                table: "EvaluationDevices");
        }
    }
}
