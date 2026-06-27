using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VolunteerTaskExternalKeyAndAllocationRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "VolunteerTasks",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExternalKey",
                table: "VolunteerTasks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "TaskAllocationDrafts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TargetRole",
                table: "TaskAllocationDrafts",
                type: "int",
                nullable: false,
                defaultValue: 3);

            // Backfill existing rows with a unique GUID before the UNIQUE index is
            // created — the AddColumn above gives every existing row the same zero
            // GUID, which would violate uniqueness. NEWID() yields a distinct value
            // per row. New rows get their GUID from the entity (Guid.NewGuid()).
            migrationBuilder.Sql(
                "UPDATE [VolunteerTasks] SET [ExternalKey] = NEWID() " +
                "WHERE [ExternalKey] = '00000000-0000-0000-0000-000000000000';");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_ExternalKey",
                table: "VolunteerTasks",
                column: "ExternalKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VolunteerTasks_ExternalKey",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "ExternalKey",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "TaskAllocationDrafts");

            migrationBuilder.DropColumn(
                name: "TargetRole",
                table: "TaskAllocationDrafts");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "VolunteerTasks",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000,
                oldNullable: true);
        }
    }
}
