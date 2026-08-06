using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class MovePersonMentionUrnToParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 🔒 §884.1 — ORDER MATTERS. EF scaffolded the DROPs FIRST, which would have discarded
            // every URN already resolved in PROD (16 of them, from the 2026-08-06 00:10Z run).
            // Add → COPY → drop, so the move keeps the data instead of re-earning it against a
            // day-throttled endpoint.
            migrationBuilder.AddColumn<string>(
                name: "LinkedInPersonUrn",
                table: "Participants",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LinkedInPersonUrnCheckedAt",
                table: "Participants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkedInPersonUrnStatus",
                table: "Participants",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkedInVanityName",
                table: "Participants",
                type: "nvarchar(max)",
                nullable: true);

            // Carry the already-resolved speakers across before the source columns go away.
            migrationBuilder.Sql(@"
                UPDATE p
                   SET p.LinkedInPersonUrn          = sp.LinkedInPersonUrn,
                       p.LinkedInPersonUrnStatus    = sp.LinkedInPersonUrnStatus,
                       p.LinkedInPersonUrnCheckedAt = sp.LinkedInPersonUrnCheckedAt
                  FROM Participants p
                  JOIN SpeakerProfiles sp ON sp.ParticipantId = p.Id
                 WHERE sp.LinkedInPersonUrn IS NOT NULL
                    OR sp.LinkedInPersonUrnStatus IS NOT NULL;");

            migrationBuilder.DropColumn(name: "LinkedInPersonUrn", table: "SpeakerProfiles");
            migrationBuilder.DropColumn(name: "LinkedInPersonUrnCheckedAt", table: "SpeakerProfiles");
            migrationBuilder.DropColumn(name: "LinkedInPersonUrnStatus", table: "SpeakerProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 🔒 Mirror of Up(): add → COPY BACK → drop. A rollback that silently emptied the cache
            // would look like "nobody follows the page" on the next run, which is the §858.16h
            // failure mode all over again. Sponsor contacts have no speaker row, so their URNs
            // cannot survive a rollback — that is a real loss and is stated rather than hidden.
            migrationBuilder.AddColumn<string>(
                name: "LinkedInPersonUrn",
                table: "SpeakerProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LinkedInPersonUrnCheckedAt",
                table: "SpeakerProfiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkedInPersonUrnStatus",
                table: "SpeakerProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE sp
                   SET sp.LinkedInPersonUrn          = p.LinkedInPersonUrn,
                       sp.LinkedInPersonUrnStatus    = p.LinkedInPersonUrnStatus,
                       sp.LinkedInPersonUrnCheckedAt = p.LinkedInPersonUrnCheckedAt
                  FROM SpeakerProfiles sp
                  JOIN Participants p ON sp.ParticipantId = p.Id
                 WHERE p.LinkedInPersonUrn IS NOT NULL
                    OR p.LinkedInPersonUrnStatus IS NOT NULL;");

            migrationBuilder.DropColumn(name: "LinkedInPersonUrn", table: "Participants");
            migrationBuilder.DropColumn(name: "LinkedInPersonUrnCheckedAt", table: "Participants");
            migrationBuilder.DropColumn(name: "LinkedInPersonUrnStatus", table: "Participants");
            migrationBuilder.DropColumn(name: "LinkedInVanityName", table: "Participants");
        }
    }
}
