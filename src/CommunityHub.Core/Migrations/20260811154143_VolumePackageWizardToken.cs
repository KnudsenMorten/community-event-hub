using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VolumePackageWizardToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WizardCompletedAt",
                table: "VolumePackageCompanies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WizardInvitedAt",
                table: "VolumePackageCompanies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WizardLastOpenedAt",
                table: "VolumePackageCompanies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WizardOpenCount",
                table: "VolumePackageCompanies",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WizardToken",
                table: "VolumePackageCompanies",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WizardTokenExpiresAt",
                table: "VolumePackageCompanies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WizardTokenIssuedAt",
                table: "VolumePackageCompanies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WizardTokenRevokedAt",
                table: "VolumePackageCompanies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolumePackageCompanies_WizardToken",
                table: "VolumePackageCompanies",
                column: "WizardToken",
                unique: true,
                filter: "[WizardToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VolumePackageCompanies_WizardToken",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "WizardCompletedAt",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "WizardInvitedAt",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "WizardLastOpenedAt",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "WizardOpenCount",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "WizardToken",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "WizardTokenExpiresAt",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "WizardTokenIssuedAt",
                table: "VolumePackageCompanies");

            migrationBuilder.DropColumn(
                name: "WizardTokenRevokedAt",
                table: "VolumePackageCompanies");
        }
    }
}
