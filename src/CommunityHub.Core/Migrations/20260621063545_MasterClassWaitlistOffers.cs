using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class MasterClassWaitlistOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MasterClassSignups_EventId_AttendeeId",
                table: "MasterClassSignups");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OfferExpiresAt",
                table: "MasterClassSignups",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MasterClassSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OfferHoldHours = table.Column<int>(type: "int", nullable: false),
                    PromotionMode = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterClassSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterClassSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_EventId_AttendeeId",
                table: "MasterClassSignups",
                columns: new[] { "EventId", "AttendeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_EventId_AttendeeId_SessionId",
                table: "MasterClassSignups",
                columns: new[] { "EventId", "AttendeeId", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSettings_EventId",
                table: "MasterClassSettings",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MasterClassSettings");

            migrationBuilder.DropIndex(
                name: "IX_MasterClassSignups_EventId_AttendeeId",
                table: "MasterClassSignups");

            migrationBuilder.DropIndex(
                name: "IX_MasterClassSignups_EventId_AttendeeId_SessionId",
                table: "MasterClassSignups");

            migrationBuilder.DropColumn(
                name: "OfferExpiresAt",
                table: "MasterClassSignups");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_EventId_AttendeeId",
                table: "MasterClassSignups",
                columns: new[] { "EventId", "AttendeeId" },
                unique: true);
        }
    }
}
