using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class RolloutGroupRings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GroupOverride",
                table: "FeatureSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReleasedToRingOverride",
                table: "FeatureSettings",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FeatureGroupSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<int>(type: "int", nullable: false),
                    ReleasedToRing = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureGroupSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeatureGroupSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureGroupSettings_EventId_Group",
                table: "FeatureGroupSettings",
                columns: new[] { "EventId", "Group" },
                unique: true);

            // Data migration (§23a, zero behaviour change): every EXISTING feature
            // row keeps its current released ring as an explicit per-feature
            // OVERRIDE, so the new group-ring resolution (override ?? group ?? catalog)
            // returns exactly the same effective ring as before this migration. New
            // rows (no override) inherit the group/catalog default; organizers can
            // "clear override" to adopt a group ring going forward.
            migrationBuilder.Sql(
                "UPDATE [FeatureSettings] SET [ReleasedToRingOverride] = [ReleasedToRing];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatureGroupSettings");

            migrationBuilder.DropColumn(
                name: "GroupOverride",
                table: "FeatureSettings");

            migrationBuilder.DropColumn(
                name: "ReleasedToRingOverride",
                table: "FeatureSettings");
        }
    }
}
