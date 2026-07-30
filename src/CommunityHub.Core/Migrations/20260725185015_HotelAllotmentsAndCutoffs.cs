using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class HotelAllotmentsAndCutoffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HotelAllotments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    HotelId = table.Column<int>(type: "int", nullable: false),
                    Night = table.Column<DateOnly>(type: "date", nullable: false),
                    RoomsAllotted = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelAllotments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelAllotments_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HotelAllotments_Hotels_HotelId",
                        column: x => x.HotelId,
                        principalTable: "Hotels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HotelCutoffs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    HotelId = table.Column<int>(type: "int", nullable: false),
                    CutoffDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReleasePercent = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelCutoffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelCutoffs_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HotelCutoffs_Hotels_HotelId",
                        column: x => x.HotelId,
                        principalTable: "Hotels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HotelAllotments_EventId_HotelId_Night",
                table: "HotelAllotments",
                columns: new[] { "EventId", "HotelId", "Night" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelAllotments_HotelId",
                table: "HotelAllotments",
                column: "HotelId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelCutoffs_EventId_CutoffDate",
                table: "HotelCutoffs",
                columns: new[] { "EventId", "CutoffDate" });

            migrationBuilder.CreateIndex(
                name: "IX_HotelCutoffs_EventId_HotelId_CutoffDate",
                table: "HotelCutoffs",
                columns: new[] { "EventId", "HotelId", "CutoffDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelCutoffs_HotelId",
                table: "HotelCutoffs",
                column: "HotelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HotelAllotments");

            migrationBuilder.DropTable(
                name: "HotelCutoffs");
        }
    }
}
