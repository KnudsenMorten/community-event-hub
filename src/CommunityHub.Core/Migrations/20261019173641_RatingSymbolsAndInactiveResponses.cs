using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class RatingSymbolsAndInactiveResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KioskRatingSymbols",
                table: "Events",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QrRatingSymbols",
                table: "Events",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InactivatedAt",
                table: "EvaluationResponses",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsInactive",
                table: "EvaluationResponses",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RatingSymbols",
                table: "EvaluationResponses",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QrRatingSymbols",
                table: "EvaluationPhysicalRooms",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RatingSymbols",
                table: "EvaluationDevices",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KioskRatingSymbols",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "QrRatingSymbols",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "InactivatedAt",
                table: "EvaluationResponses");

            migrationBuilder.DropColumn(
                name: "IsInactive",
                table: "EvaluationResponses");

            migrationBuilder.DropColumn(
                name: "RatingSymbols",
                table: "EvaluationResponses");

            migrationBuilder.DropColumn(
                name: "QrRatingSymbols",
                table: "EvaluationPhysicalRooms");

            migrationBuilder.DropColumn(
                name: "RatingSymbols",
                table: "EvaluationDevices");
        }
    }
}
