using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SponsorInfos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LogoVectorPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LogoVectorFileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LogoRasterPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LogoRasterFileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CompanyDescriptionShort = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    SocialMediaIntro = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorInfos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorInfos_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorInfos_EventId_SponsorCompanyId",
                table: "SponsorInfos",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SponsorInfos");
        }
    }
}
