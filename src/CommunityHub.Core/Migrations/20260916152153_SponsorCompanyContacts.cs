using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorCompanyContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SponsorCompanyContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsSigner = table.Column<bool>(type: "bit", nullable: false),
                    IsEventCoordinator = table.Column<bool>(type: "bit", nullable: false),
                    IsBoothMember = table.Column<bool>(type: "bit", nullable: false),
                    ErpContactNumber = table.Column<int>(type: "int", nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorCompanyContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorCompanyContacts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SponsorCompanyContacts_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorCompanyContacts_EventId_ParticipantId_SponsorCompanyId",
                table: "SponsorCompanyContacts",
                columns: new[] { "EventId", "ParticipantId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorCompanyContacts_EventId_SponsorCompanyId",
                table: "SponsorCompanyContacts",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorCompanyContacts_EventId_SponsorCompanyId_ErpContactNumber",
                table: "SponsorCompanyContacts",
                columns: new[] { "EventId", "SponsorCompanyId", "ErpContactNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorCompanyContacts_ParticipantId",
                table: "SponsorCompanyContacts",
                column: "ParticipantId");

            // §1235 BACKFILL — every existing sponsor contact gets the link it already implies.
            //
            // 🔑 Measured before writing this (PROD, 2026-09-16): NO e-mail is held by two participant
            // rows and NO company shares a coordinator address, so today's state maps 1:1 — each
            // contact has exactly one company, and that company is their PRIMARY. There is no
            // ambiguity to resolve and nothing to merge.
            //
            // 🔒 Flags are copied AS THEY STAND: the person-level IsSigner / IsEventCoordinator /
            // IsBoothMember have only ever meant "for my one company", so the link inherits them
            // unchanged. The Participant columns stay in place and keep serving every existing
            // reader (stage 1 is additive).
            //
            // ⚠️ Idempotent by the NOT EXISTS: re-running writes nothing.
            migrationBuilder.Sql(@"
INSERT INTO SponsorCompanyContacts
    (EventId, ParticipantId, SponsorCompanyId, IsSigner, IsEventCoordinator, IsBoothMember,
     ErpContactNumber, IsPrimary, CreatedAt)
SELECT p.EventId, p.Id, p.SponsorCompanyId, p.IsSigner, p.IsEventCoordinator, p.IsBoothMember,
       p.ErpContactNumber, 1, SYSDATETIMEOFFSET()
  FROM Participants p
 WHERE p.SponsorCompanyId IS NOT NULL
   AND LTRIM(RTRIM(p.SponsorCompanyId)) <> ''
   AND NOT EXISTS (SELECT 1 FROM SponsorCompanyContacts c
                    WHERE c.EventId = p.EventId AND c.ParticipantId = p.Id
                      AND c.SponsorCompanyId = p.SponsorCompanyId);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SponsorCompanyContacts");
        }
    }
}
