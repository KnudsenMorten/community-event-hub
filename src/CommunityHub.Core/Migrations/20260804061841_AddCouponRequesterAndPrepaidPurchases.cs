using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCouponRequesterAndPrepaidPurchases : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// 🔴 <b>HAND-ORDERED. The scaffold dropped the five columns BEFORE creating the table that
        /// replaces them</b> — EF said so out loud (*"An operation was scaffolded that may result in
        /// the loss of data"*) — which would have thrown every existing pool's quantity and invoice
        /// number away. The order here is <b>create → MOVE → drop</b>, so an existing allocation
        /// becomes its own first purchase row and nothing is lost.
        ///
        /// <para>⚠️ PROD had no prepaid pools when this was written (measured 2026-08-04 05:43 UTC),
        /// but a migration that is only correct against an empty table is a landmine for DEV and for
        /// any pool created between then and the deploy.</para>
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequesterContactNumber",
                table: "CouponInvoicingSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequesterName",
                table: "CouponInvoicingSettings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CouponPrepaidPurchases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CouponPrepaidAllocationId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    ErpInvoiceNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ErpInvoiceConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ErpInvoiceConfirmedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    LastBillingReminderAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponPrepaidPurchases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CouponPrepaidPurchases_CouponPrepaidAllocations_CouponPrepaidAllocationId",
                        column: x => x.CouponPrepaidAllocationId,
                        principalTable: "CouponPrepaidAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CouponPrepaidPurchases_CouponPrepaidAllocationId",
                table: "CouponPrepaidPurchases",
                column: "CouponPrepaidAllocationId");

            // 🔴 §798.4 — MOVE, do not drop. Every existing pool becomes its own FIRST purchase,
            // carrying the quantity, the invoice number that confirmed it and who confirmed it. A
            // pool with 0 bought is skipped: it was never an agreement, and a 0-ticket purchase row
            // would read as one.
            migrationBuilder.Sql("""
                INSERT INTO [CouponPrepaidPurchases]
                    ([CouponPrepaidAllocationId], [Quantity], [ErpInvoiceNumber],
                     [ErpInvoiceConfirmedAt], [ErpInvoiceConfirmedByEmail],
                     [LastBillingReminderAt], [Notes], [CreatedAt], [CreatedByEmail])
                SELECT [Id], [QuantityPurchased], [ErpInvoiceNumber],
                       [ErpInvoiceConfirmedAt], [ErpInvoiceConfirmedByEmail],
                       [LastBillingReminderAt], NULL, [CreatedAt], [LastUpdatedByEmail]
                FROM [CouponPrepaidAllocations]
                WHERE [QuantityPurchased] > 0;
                """);

            migrationBuilder.DropColumn(
                name: "ErpInvoiceConfirmedAt",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "ErpInvoiceConfirmedByEmail",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "ErpInvoiceNumber",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "LastBillingReminderAt",
                table: "CouponPrepaidAllocations");

            migrationBuilder.DropColumn(
                name: "QuantityPurchased",
                table: "CouponPrepaidAllocations");
        }

        /// <inheritdoc />
        /// <remarks>
        /// ⚠️ <b>Down is LOSSY and cannot be otherwise:</b> a pool with three top-ups has no single
        /// quantity or invoice number to put back. The columns return empty; the purchase history is
        /// gone with the table. Rolling this back is a code rollback, not a data recovery.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CouponPrepaidPurchases");

            migrationBuilder.DropColumn(
                name: "RequesterContactNumber",
                table: "CouponInvoicingSettings");

            migrationBuilder.DropColumn(
                name: "RequesterName",
                table: "CouponInvoicingSettings");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ErpInvoiceConfirmedAt",
                table: "CouponPrepaidAllocations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpInvoiceConfirmedByEmail",
                table: "CouponPrepaidAllocations",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpInvoiceNumber",
                table: "CouponPrepaidAllocations",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastBillingReminderAt",
                table: "CouponPrepaidAllocations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuantityPurchased",
                table: "CouponPrepaidAllocations",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
