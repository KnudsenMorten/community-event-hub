using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class MailCampaignRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailCampaignRecipients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MailCampaignId = table.Column<int>(type: "int", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailCampaignRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailCampaignRecipients_MailCampaigns_MailCampaignId",
                        column: x => x.MailCampaignId,
                        principalTable: "MailCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailCampaignRecipients_MailCampaignId_Email",
                table: "MailCampaignRecipients",
                columns: new[] { "MailCampaignId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailCampaignRecipients_MailCampaignId_State",
                table: "MailCampaignRecipients",
                columns: new[] { "MailCampaignId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailCampaignRecipients");
        }
    }
}
