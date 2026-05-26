using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class TravelClaimAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CannotStayInLimits",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "SessionType",
                table: "TravelReimbursements");

            migrationBuilder.RenameColumn(
                name: "EstimatedFlightCostEur",
                table: "TravelReimbursements",
                newName: "ClaimAmountEur");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ClaimAmountEur",
                table: "TravelReimbursements",
                newName: "EstimatedFlightCostEur");

            migrationBuilder.AddColumn<bool>(
                name: "CannotStayInLimits",
                table: "TravelReimbursements",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SessionType",
                table: "TravelReimbursements",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);
        }
    }
}
