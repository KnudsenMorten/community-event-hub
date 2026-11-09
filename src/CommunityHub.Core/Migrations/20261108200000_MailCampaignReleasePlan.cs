using System;
using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §1388 — the staged release plan of a mail campaign: preview to the organizers, a first wave of the audience, then
    /// everyone; when each step ran, the wave size, and the Stop marker. Additive.
    /// </summary>
    /// <remarks>Hand-written (no local build on the management host); the model snapshot carries the same columns.</remarks>
    [DbContext(typeof(CommunityHubDbContext))]
    [Migration("20261108200000_MailCampaignReleasePlan")]
    public partial class MailCampaignReleasePlan : Migration
    {
        private static readonly string[] Times =
        {
            "PlanPreviewAt", "PlanWaveAt", "PlanEveryoneAt", "PlanPreviewDoneAt", "PlanWaveDoneAt", "PlanEveryoneDoneAt", "PlanStoppedAt",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var name in Times)
                migrationBuilder.AddColumn<DateTimeOffset>(name: name, table: "MailCampaigns", type: "datetimeoffset", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PlanStoppedByEmail", table: "MailCampaigns", type: "nvarchar(320)", maxLength: 320, nullable: true);
            migrationBuilder.AddColumn<string>(name: "PlanSetByEmail", table: "MailCampaigns", type: "nvarchar(320)", maxLength: 320, nullable: true);
            migrationBuilder.AddColumn<int>(name: "PlanWavePercent", table: "MailCampaigns", type: "int", nullable: false, defaultValue: 5);
            migrationBuilder.AddColumn<int>(name: "PlanWaveSize", table: "MailCampaigns", type: "int", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var name in Times)
                migrationBuilder.DropColumn(name: name, table: "MailCampaigns");
            migrationBuilder.DropColumn(name: "PlanStoppedByEmail", table: "MailCampaigns");
            migrationBuilder.DropColumn(name: "PlanSetByEmail", table: "MailCampaigns");
            migrationBuilder.DropColumn(name: "PlanWavePercent", table: "MailCampaigns");
            migrationBuilder.DropColumn(name: "PlanWaveSize", table: "MailCampaigns");
        }
    }
}
