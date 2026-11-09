using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VisaApplicationForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsVisa",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "VisaApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    FirstNames = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Nationality = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    HomeStreet = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HomePostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    HomeCity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HomeCountry = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyStreet = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyPostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CompanyCity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyCountry = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    CompanyPhone = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CompanyWebsite = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DepartmentAndRole = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    VisitedBefore = table.Column<bool>(type: "bit", nullable: true),
                    CompanyActivity = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ContactOrigin = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RelationshipDuration = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FinancialScope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ArrivalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LengthOfVisitDays = table.Column<int>(type: "int", nullable: true),
                    Entries = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SpecialCircumstances = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TravelPaidBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StayPaidBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Accommodation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HostGuarantees = table.Column<bool>(type: "bit", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApplicationId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApplicationIdSetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisaApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisaApplications_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisaApplications_EventId",
                table: "VisaApplications",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_VisaApplications_ParticipantId",
                table: "VisaApplications",
                column: "ParticipantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VisaApplications");

            migrationBuilder.DropColumn(
                name: "NeedsVisa",
                table: "Participants");
        }
    }
}
