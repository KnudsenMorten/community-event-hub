using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LifecycleState",
                table: "Participants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "OnboardingCompleted_Appreciation",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OnboardingCompleted_Bio",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OnboardingCompleted_Hotel",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OnboardingCompleted_Picture",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OnboardingCompleted_Swag",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "QueueSource",
                table: "Participants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Participants_EventId_LifecycleState",
                table: "Participants",
                columns: new[] { "EventId", "LifecycleState" });

            // Backfill: every participant that already existed before the
            // onboarding lifecycle was introduced is a real, already-onboarded
            // person — promote them to Active (2) so the new combined login gate
            // (IsActive AND LifecycleState == Active) does NOT lock them out.
            // New rows are written by code with an explicit LifecycleState, so
            // the column default (0 = Inactive) only ever applies to brand-new
            // queue entries that the code immediately sets.
            migrationBuilder.Sql(
                "UPDATE [Participants] SET [LifecycleState] = 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Participants_EventId_LifecycleState",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "LifecycleState",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_Appreciation",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_Bio",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_Hotel",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_Picture",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_Swag",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "QueueSource",
                table: "Participants");
        }
    }
}
