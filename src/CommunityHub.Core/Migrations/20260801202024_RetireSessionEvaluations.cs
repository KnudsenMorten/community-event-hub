using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class RetireSessionEvaluations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §748.1 — DROP ONLY IF EMPTY. Deliberately not EF's plain DropTable.
            //
            // 🔒 The retirement is safe on the evidence: §748 verified `SessionEvaluations 0` on PROD
            // (2026-07-31), and since then NO code path could write a row — the public submit page
            // has only redirected. But that count could NOT be re-verified from this session: the
            // deployment SPN has no SQL login ("Login failed for user '<token-identified
            // principal>'"), so the last direct observation is a day old.
            //
            // An unverifiable DROP of a table that MIGHT hold attendee feedback is not a risk worth
            // running for a cleanup. This drops it when it is empty — the expected case, and the
            // point of the section — and otherwise leaves it alone. The C# model no longer maps it
            // either way, so a surviving table is inert, not broken.
            //
            // ⇒ If the table still exists after this ships, it HAD ROWS. Look at them before
            // dropping it by hand; do not assume the guard misfired.
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[SessionEvaluations]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM [SessionEvaluations])
        DROP TABLE [SessionEvaluations];
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessionEvaluations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VoterKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionEvaluations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SessionEvaluations_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvaluations_EventId_IpHash",
                table: "SessionEvaluations",
                columns: new[] { "EventId", "IpHash" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvaluations_EventId_SessionId",
                table: "SessionEvaluations",
                columns: new[] { "EventId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvaluations_SessionId_VoterKey",
                table: "SessionEvaluations",
                columns: new[] { "SessionId", "VoterKey" },
                unique: true,
                filter: "[VoterKey] IS NOT NULL");
        }
    }
}
