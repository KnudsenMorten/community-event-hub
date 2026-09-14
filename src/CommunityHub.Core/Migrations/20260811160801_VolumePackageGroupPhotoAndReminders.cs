using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VolumePackageGroupPhotoAndReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WizardRemindedAt",
                table: "VolumePackageCompanies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WizardReminderCount",
                table: "VolumePackageCompanies",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "VolumePackageCompanyId",
                table: "GroupPhotoRegistrations",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupPhotoRegistrations_VolumePackageCompanyId",
                table: "GroupPhotoRegistrations",
                column: "VolumePackageCompanyId");

            migrationBuilder.AddForeignKey(
                name: "FK_GroupPhotoRegistrations_VolumePackageCompanies_VolumePackageCompanyId",
                table: "GroupPhotoRegistrations",
                column: "VolumePackageCompanyId",
                principalTable: "VolumePackageCompanies",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GroupPhotoRegistrations_VolumePackageCompanies_VolumePackageCompanyId",
                table: "GroupPhotoRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_GroupPhotoRegistrations_VolumePackageCompanyId",
                table: "GroupPhotoRegistrations");

            migrationBuilder.DropColumn(
                name: "WizardRemindedAt",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "WizardReminderCount",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "VolumePackageCompanyId",
                table: "GroupPhotoRegistrations");
        }
    }
}
