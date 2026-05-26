using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CommunityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PreDayDate = table.Column<DateOnly>(type: "date", nullable: true),
                    VenueName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    HubHostname = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LockDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Attendees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TicketStatus = table.Column<int>(type: "int", nullable: false),
                    TicketClassName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BookingStatus = table.Column<int>(type: "int", nullable: false),
                    MasterClassName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HasReconciliationMismatch = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attendees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attendees_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Participants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Participants_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SentReminders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ReminderType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccasionKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentReminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SentReminders_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DinnerSignups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Attending = table.Column<bool>(type: "bit", nullable: false),
                    DietaryPreference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AllergyNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PlusOne = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DinnerSignups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DinnerSignups_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DinnerSignups_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HotelBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    NeedsRoom = table.Column<bool>(type: "bit", nullable: false),
                    CheckInDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CheckOutDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RoomShareWith = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelBookings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HotelBookings_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoginPins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    PinHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginPins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoginPins_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    AssignedParticipantId = table.Column<int>(type: "int", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tasks_Participants_AssignedParticipantId",
                        column: x => x.AssignedParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerAvailabilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    SelectedShifts = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PreferredRole = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MaxHoursPerDay = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerAvailabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerAvailabilities_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerAvailabilities_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_Email",
                table: "Attendees",
                columns: new[] { "EventId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_HasReconciliationMismatch",
                table: "Attendees",
                columns: new[] { "EventId", "HasReconciliationMismatch" });

            migrationBuilder.CreateIndex(
                name: "IX_DinnerSignups_EventId_ParticipantId",
                table: "DinnerSignups",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DinnerSignups_ParticipantId",
                table: "DinnerSignups",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_Code",
                table: "Events",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_IsActive",
                table: "Events",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_EventId_ParticipantId",
                table: "HotelBookings",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_ParticipantId",
                table: "HotelBookings",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_LoginPins_ParticipantId_ExpiresAt",
                table: "LoginPins",
                columns: new[] { "ParticipantId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Participants_EventId_Email",
                table: "Participants",
                columns: new[] { "EventId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_EventId_RecipientEmail_ReminderType_OccasionKey",
                table: "SentReminders",
                columns: new[] { "EventId", "RecipientEmail", "ReminderType", "OccasionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_AssignedParticipantId",
                table: "Tasks",
                column: "AssignedParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_EventId_AssignedParticipantId",
                table: "Tasks",
                columns: new[] { "EventId", "AssignedParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_EventId_DueDate",
                table: "Tasks",
                columns: new[] { "EventId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_EventId_SponsorCompanyId",
                table: "Tasks",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerAvailabilities_EventId_ParticipantId",
                table: "VolunteerAvailabilities",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerAvailabilities_ParticipantId",
                table: "VolunteerAvailabilities",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attendees");

            migrationBuilder.DropTable(
                name: "DinnerSignups");

            migrationBuilder.DropTable(
                name: "HotelBookings");

            migrationBuilder.DropTable(
                name: "LoginPins");

            migrationBuilder.DropTable(
                name: "SentReminders");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "VolunteerAvailabilities");

            migrationBuilder.DropTable(
                name: "Participants");

            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
