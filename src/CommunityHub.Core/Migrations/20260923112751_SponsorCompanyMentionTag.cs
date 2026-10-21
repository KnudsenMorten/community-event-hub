using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorCompanyMentionTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LinkedInOrganizationCheckedAt",
                table: "SponsorInfos",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkedInOrganizationLookedUpFrom",
                table: "SponsorInfos",
                type: "nvarchar(max)",
                nullable: true);

            // §1300 — the Sponsor announcement TAGS the company first in the Tag line and names it in
            // the sentence (operator 2026-09-23). Planned posts store the TEMPLATE text (tokens intact,
            // values filled at render), frozen when planned — so the catalog change alone would reach
            // only posts the planner re-plans. Same shape as §935's StandardiseOrganizerTokenInStoredPosts:
            // posts, overrides and samples. 🔒 UNPUBLISHED posts only — a published post is history.
            foreach (var (table, column, where) in Targets)
            {
                migrationBuilder.Sql($@"
UPDATE {table} SET {column} = REPLACE({column}, N'Tag: {{SponsorSigner}}', N'Tag: {{SponsorCompanyMention}} {{SponsorSigner}}')
 WHERE {column} LIKE N'%Tag: {{SponsorSigner}}%' AND {column} NOT LIKE N'%{{SponsorCompanyMention}}%'{where};");
                migrationBuilder.Sql($@"
UPDATE {table} SET {column} = REPLACE({column}, N'announce {{SponsorLinkedInUrl}} as', N'announce {{SponsorName}} as')
 WHERE {column} LIKE N'%announce {{SponsorLinkedInUrl}} as%'{where};");
            }
        }

        private const string Unpublished = " AND PublishedAtUtc IS NULL AND ExternalPostId IS NULL";

        private static readonly (string Table, string Column, string Where)[] Targets =
        {
            ("SoMePosts", "AutoText", Unpublished),
            ("SoMePosts", "ManualTextOverride", Unpublished),
            ("SoMeTemplates", "Body", ""),
            ("SoMeBodySamples", "Body", ""),
        };

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column, where) in Targets)
            {
                migrationBuilder.Sql($@"
UPDATE {table} SET {column} = REPLACE({column}, N'Tag: {{SponsorCompanyMention}} {{SponsorSigner}}', N'Tag: {{SponsorSigner}}')
 WHERE {column} LIKE N'%Tag: {{SponsorCompanyMention}} {{SponsorSigner}}%'{where};");
                migrationBuilder.Sql($@"
UPDATE {table} SET {column} = REPLACE({column}, N'announce {{SponsorName}} as', N'announce {{SponsorLinkedInUrl}} as')
 WHERE {column} LIKE N'%announce {{SponsorName}} as%'{where};");
            }

            migrationBuilder.DropColumn(
                name: "LinkedInOrganizationCheckedAt",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "LinkedInOrganizationLookedUpFrom",
                table: "SponsorInfos");
        }
    }
}
