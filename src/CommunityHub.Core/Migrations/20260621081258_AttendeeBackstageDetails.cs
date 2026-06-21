using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AttendeeBackstageDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomFieldsJson",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobTitle",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrderId",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Postcode",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxId",
                table: "Attendees",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "City",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "CustomFieldsJson",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "JobTitle",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "Postcode",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "TaxId",
                table: "Attendees");
        }
    }
}
