using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class DocLibraryPathOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocLibrarySettingChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocLibrarySettingChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocLibrarySettingOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocLibrarySettingOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocLibrarySettingChanges_ChangedAt",
                table: "DocLibrarySettingChanges",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DocLibrarySettingChanges_Kind_Key_ChangedAt",
                table: "DocLibrarySettingChanges",
                columns: new[] { "Kind", "Key", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DocLibrarySettingOverrides_Kind_Key",
                table: "DocLibrarySettingOverrides",
                columns: new[] { "Kind", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocLibrarySettingChanges");

            migrationBuilder.DropTable(
                name: "DocLibrarySettingOverrides");
        }
    }
}
