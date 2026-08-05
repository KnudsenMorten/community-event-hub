using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class EvaluationSerialNumberRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EvaluationResponses_DeviceId_DeviceRecordId",
                table: "EvaluationResponses");

            migrationBuilder.DropIndex(
                name: "IX_EvaluationDevices_EventId_DeviceId",
                table: "EvaluationDevices");

            migrationBuilder.DropIndex(
                name: "IX_EvaluationDeviceProvisionRequests_EventId_DeviceId",
                table: "EvaluationDeviceProvisionRequests");

            // 🔴 HAND-CORRECTED. EF scaffolded `DropColumn("DeviceId")` for these two tables and kept
            // the SerialNumber column that §753 had briefly added — because from the model diff it
            // cannot tell that one was RENAMED onto the other. Run as generated, it would have
            // dropped every device's identity and left the empty column in its place: silent data
            // loss, and invisible today only because both environments have zero device rows.
            //
            // The correct order is: drop the EMPTY new column first, then rename the populated one
            // into its place. Same end state, no data destroyed on a database that has units.
            migrationBuilder.DropColumn(
                name: "SerialNumber",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "SerialNumber",
                table: "EvaluationDeviceProvisionRequests");

            migrationBuilder.RenameColumn(
                name: "DeviceId",
                table: "EvaluationDevices",
                newName: "SerialNumber");

            migrationBuilder.RenameColumn(
                name: "DeviceId",
                table: "EvaluationDeviceProvisionRequests",
                newName: "SerialNumber");

            migrationBuilder.RenameColumn(
                name: "DeviceId",
                table: "EvaluationResponses",
                newName: "SerialNumber");

            migrationBuilder.AlterColumn<string>(
                name: "SerialNumber",
                table: "EvaluationDevices",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SerialNumber",
                table: "EvaluationDeviceProvisionRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationResponses_SerialNumber_DeviceRecordId",
                table: "EvaluationResponses",
                columns: new[] { "SerialNumber", "DeviceRecordId" },
                unique: true,
                filter: "[SerialNumber] IS NOT NULL AND [DeviceRecordId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_EventId_SerialNumber",
                table: "EvaluationDevices",
                columns: new[] { "EventId", "SerialNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDeviceProvisionRequests_EventId_SerialNumber",
                table: "EvaluationDeviceProvisionRequests",
                columns: new[] { "EventId", "SerialNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EvaluationResponses_SerialNumber_DeviceRecordId",
                table: "EvaluationResponses");

            migrationBuilder.DropIndex(
                name: "IX_EvaluationDevices_EventId_SerialNumber",
                table: "EvaluationDevices");

            migrationBuilder.DropIndex(
                name: "IX_EvaluationDeviceProvisionRequests_EventId_SerialNumber",
                table: "EvaluationDeviceProvisionRequests");

            // 🔴 HAND-CORRECTED to mirror the corrected Up(). The scaffolded Down() renamed only
            // EvaluationResponses and then ADDED an empty DeviceId beside the still-populated
            // SerialNumber — so rolling back would have left the identity in the wrong column and
            // the fleet unable to authenticate. A rollback that does not restore the previous state
            // is worse than no rollback, because it is trusted.
            migrationBuilder.RenameColumn(
                name: "SerialNumber",
                table: "EvaluationResponses",
                newName: "DeviceId");

            migrationBuilder.RenameColumn(
                name: "SerialNumber",
                table: "EvaluationDevices",
                newName: "DeviceId");

            migrationBuilder.RenameColumn(
                name: "SerialNumber",
                table: "EvaluationDeviceProvisionRequests",
                newName: "DeviceId");

            // The nullable SerialNumber columns §753 briefly had, restored empty.
            migrationBuilder.AddColumn<string>(
                name: "SerialNumber",
                table: "EvaluationDevices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SerialNumber",
                table: "EvaluationDeviceProvisionRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationResponses_DeviceId_DeviceRecordId",
                table: "EvaluationResponses",
                columns: new[] { "DeviceId", "DeviceRecordId" },
                unique: true,
                filter: "[DeviceId] IS NOT NULL AND [DeviceRecordId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_EventId_DeviceId",
                table: "EvaluationDevices",
                columns: new[] { "EventId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDeviceProvisionRequests_EventId_DeviceId",
                table: "EvaluationDeviceProvisionRequests",
                columns: new[] { "EventId", "DeviceId" },
                unique: true);
        }
    }
}
