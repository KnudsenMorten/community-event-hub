using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SoMePostSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "SoMePosts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByEmail",
                table: "SoMePosts",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "SoMePosts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_SoMePosts_EventId_IsDeleted",
                table: "SoMePosts",
                columns: new[] { "EventId", "IsDeleted" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SoMePosts_EventId_IsDeleted",
                table: "SoMePosts");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "SoMePosts");

            migrationBuilder.DropColumn(
                name: "DeletedByEmail",
                table: "SoMePosts");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "SoMePosts");
        }
    }
}
