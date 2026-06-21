using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class MasterClassSignup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MasterClassCapacity",
                table: "Sessions",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MasterClassSignups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    AttendeeId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PromotionNotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterClassSignups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterClassSignups_Attendees_AttendeeId",
                        column: x => x.AttendeeId,
                        principalTable: "Attendees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassSignups_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MasterClassSignups_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_AttendeeId",
                table: "MasterClassSignups",
                column: "AttendeeId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_EventId_AttendeeId",
                table: "MasterClassSignups",
                columns: new[] { "EventId", "AttendeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_EventId_SessionId_Status",
                table: "MasterClassSignups",
                columns: new[] { "EventId", "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_SessionId",
                table: "MasterClassSignups",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MasterClassSignups");

            migrationBuilder.DropColumn(
                name: "MasterClassCapacity",
                table: "Sessions");
        }
    }
}
