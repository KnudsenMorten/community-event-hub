using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class TaskAbandonedClosureReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClosedReason",
                table: "Tasks",
                type: "int",
                nullable: true);

            // §332 BACKFILL — label the tasks the deactivation cascade ALREADY force-closed, so
            // the corrected completion ratios apply to history too and not only to future
            // drop-outs.
            //
            // This is an EXACT match, not a heuristic: ParticipantDeactivationService stamps
            // CompletedAt and DeactivatedByOrganizerAt from the SAME `now` within one call, so a
            // Done task whose CompletedAt equals its assignee's deactivation tombstone to the
            // tick was closed BY the cascade, not by the person. Work somebody genuinely
            // completed carries an unrelated timestamp and is left alone.
            //
            // Deliberately conservative in the one ambiguous case: DeactivatedByOrganizerAt is
            // written with `??=`, so on a RE-RUN of the cascade the tombstone keeps the FIRST
            // timestamp while CompletedAt gets the newer one — those rows will not match and
            // stay counted as Done. Under-labelling merely leaves the old (over-reported)
            // number; over-labelling would silently erase real completed work from the stats.
            migrationBuilder.Sql(@"
UPDATE t
   SET t.ClosedReason = 1   -- TaskClosedReason.AbandonedOnDeactivation
  FROM Tasks t
  JOIN Participants p ON p.Id = t.AssignedParticipantId
 WHERE t.State = 2                      -- TaskState.Done
   AND t.ClosedReason IS NULL
   AND t.CompletedAt IS NOT NULL
   AND p.DeactivatedByOrganizerAt IS NOT NULL
   AND t.CompletedAt = p.DeactivatedByOrganizerAt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedReason",
                table: "Tasks");
        }
    }
}
