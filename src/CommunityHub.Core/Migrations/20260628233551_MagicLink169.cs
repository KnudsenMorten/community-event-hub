using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class MagicLink169 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUsedAt",
                table: "MagicLinkGrants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MultiUse",
                table: "MagicLinkGrants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TokenProtected",
                table: "MagicLinkGrants",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UseCount",
                table: "MagicLinkGrants",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "MagicLinkGrants");

            migrationBuilder.DropColumn(
                name: "MultiUse",
                table: "MagicLinkGrants");

            migrationBuilder.DropColumn(
                name: "TokenProtected",
                table: "MagicLinkGrants");

            migrationBuilder.DropColumn(
                name: "UseCount",
                table: "MagicLinkGrants");
        }
    }
}
