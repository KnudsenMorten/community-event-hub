using System;
using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §1387 — the dialog log of a travel claim (<c>TravelReviewLog</c>): one row per step (submit, AI check, mails,
    /// organizer decisions). Additive: a new table only.
    /// </summary>
    /// <remarks>Hand-written (no local build on the management host); the model snapshot carries the same table.</remarks>
    [DbContext(typeof(CommunityHubDbContext))]
    [Migration("20261108190000_TravelReviewLog")]
    public partial class TravelReviewLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TravelReviewLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    TravelReimbursementId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelReviewLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelReviewLog_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TravelReviewLog_EventId_TravelReimbursementId_At",
                table: "TravelReviewLog",
                columns: new[] { "EventId", "TravelReimbursementId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TravelReviewLog");
        }
    }
}
