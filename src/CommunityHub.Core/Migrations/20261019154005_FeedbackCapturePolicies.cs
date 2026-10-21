using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class FeedbackCapturePolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KioskCapturePolicy",
                table: "Events",
                type: "int",
                nullable: true);

            // §1354 — carry §1353's one-tap switch over before dropping it: one tap IS the
            // "Rating only" kiosk policy (0). Off stays null = the built-in default.
            migrationBuilder.Sql("UPDATE [Events] SET [KioskCapturePolicy] = 0 WHERE [EvaluationKioskOneTap] = 1;");

            migrationBuilder.DropColumn(
                name: "EvaluationKioskOneTap",
                table: "Events");

            migrationBuilder.AddColumn<int>(
                name: "QrCapturePolicy",
                table: "Events",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapturePolicy",
                table: "EvaluationResponses",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QrCapturePolicy",
                table: "EvaluationPhysicalRooms",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapturePolicy",
                table: "EvaluationDevices",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KioskCapturePolicy",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "QrCapturePolicy",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CapturePolicy",
                table: "EvaluationResponses");

            migrationBuilder.DropColumn(
                name: "QrCapturePolicy",
                table: "EvaluationPhysicalRooms");

            migrationBuilder.DropColumn(
                name: "CapturePolicy",
                table: "EvaluationDevices");

            migrationBuilder.AddColumn<bool>(
                name: "EvaluationKioskOneTap",
                table: "Events",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
