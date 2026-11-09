using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class TravelDocumentReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AiCheckedAt",
                table: "TravelReimbursements",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiFindingsJson",
                table: "TravelReimbursements",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiInputHash",
                table: "TravelReimbursements",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiNotifiedHash",
                table: "TravelReimbursements",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiSummary",
                table: "TravelReimbursements",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AiVerdict",
                table: "TravelReimbursements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ErpSentAt",
                table: "TravelReimbursements",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasCompany",
                table: "TravelReimbursements",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "TravelReimbursements",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewState",
                table: "TravelReimbursements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                table: "TravelReimbursements",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedByEmail",
                table: "TravelReimbursements",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SentBackCount",
                table: "TravelReimbursements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "TravelReceipts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // §1387 — claims submitted BEFORE the review existed already went to the ERP inbox on submit.
            // They enter the review as Under review with ErpSentAt stamped: they show on the organizer
            // page ("Validate all submitted"), the sweep never auto-mails about them, and Accept does not
            // send them to finance a second time. Send back clears the stamp, so corrected documents do.
            migrationBuilder.Sql(
                "UPDATE TravelReimbursements SET ReviewState = 1, ErpSentAt = SubmittedAt "
                + "WHERE SubmittedAt IS NOT NULL AND RequestReimbursement = 1 AND IsPaid = 0;");
            // Already paid claims are done: Accepted, and finance has them.
            migrationBuilder.Sql(
                "UPDATE TravelReimbursements SET ReviewState = 2, ErpSentAt = SubmittedAt "
                + "WHERE SubmittedAt IS NOT NULL AND RequestReimbursement = 1 AND IsPaid = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiCheckedAt",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "AiFindingsJson",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "AiInputHash",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "AiNotifiedHash",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "AiSummary",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "AiVerdict",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "ErpSentAt",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "HasCompany",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "ReviewState",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "ReviewedByEmail",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "SentBackCount",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "TravelReceipts");
        }
    }
}
