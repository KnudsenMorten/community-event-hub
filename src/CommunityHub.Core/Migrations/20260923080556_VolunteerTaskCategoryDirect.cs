using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VolunteerTaskCategoryDirect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerTasks_VolunteerSubcategories_SubcategoryId",
                table: "VolunteerTasks");

            migrationBuilder.AlterColumn<int>(
                name: "SubcategoryId",
                table: "VolunteerTasks",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                table: "VolunteerTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // §1299 — every existing task has a subcategory (the column was required), so its
            // category is its subcategory's. Must run BEFORE the FK to VolunteerCategories exists:
            // until then every row holds the 0 default, which that FK would refuse.
            migrationBuilder.Sql(@"
UPDATE t SET t.CategoryId = s.CategoryId
FROM VolunteerTasks t
JOIN VolunteerSubcategories s ON s.Id = t.SubcategoryId;");

            // §1299 — DISSOLVE the placeholder subcategories the hub invented (operator
            // 2026-09-23: "Dissolve them"): "Imported plan" (plan + Excel import, one per bucket)
            // and the "General" that a move-to-category created. Their tasks move directly under
            // the category; assignments and drafts hang off the TASK and are untouched.
            // 🔒 "General" only where the hub created it (its fixed description) — a "General" an
            // organizer named themselves is their structure, not a placeholder.
            migrationBuilder.Sql(@"
UPDATE t SET t.SubcategoryId = NULL
FROM VolunteerTasks t
JOIN VolunteerSubcategories s ON s.Id = t.SubcategoryId
WHERE s.Name = N'Imported plan'
   OR (s.Name = N'General' AND s.Description = N'Created automatically to receive moved tasks.');

DELETE s FROM VolunteerSubcategories s
WHERE (s.Name = N'Imported plan'
       OR (s.Name = N'General' AND s.Description = N'Created automatically to receive moved tasks.'))
  AND NOT EXISTS (SELECT 1 FROM VolunteerTasks t WHERE t.SubcategoryId = s.Id);");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_CategoryId",
                table: "VolunteerTasks",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_EventId_CategoryId",
                table: "VolunteerTasks",
                columns: new[] { "EventId", "CategoryId" });

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerTasks_VolunteerCategories_CategoryId",
                table: "VolunteerTasks",
                column: "CategoryId",
                principalTable: "VolunteerCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerTasks_VolunteerSubcategories_SubcategoryId",
                table: "VolunteerTasks",
                column: "SubcategoryId",
                principalTable: "VolunteerSubcategories",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerTasks_VolunteerCategories_CategoryId",
                table: "VolunteerTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerTasks_VolunteerSubcategories_SubcategoryId",
                table: "VolunteerTasks");

            // §1299 — rollback: the old schema requires a subcategory, so every task directly
            // under a category gets one "Imported plan" holder again (the pre-§1299 shape).
            migrationBuilder.Sql(@"
INSERT INTO VolunteerSubcategories (EventId, CategoryId, Name, CreatedAt)
SELECT DISTINCT t.EventId, t.CategoryId, N'Imported plan', SYSDATETIMEOFFSET()
FROM VolunteerTasks t
WHERE t.SubcategoryId IS NULL
  AND NOT EXISTS (SELECT 1 FROM VolunteerSubcategories s
                  WHERE s.CategoryId = t.CategoryId AND s.Name = N'Imported plan');

UPDATE t SET t.SubcategoryId = s.Id
FROM VolunteerTasks t
JOIN VolunteerSubcategories s ON s.CategoryId = t.CategoryId AND s.Name = N'Imported plan'
WHERE t.SubcategoryId IS NULL;");

            migrationBuilder.DropIndex(
                name: "IX_VolunteerTasks_CategoryId",
                table: "VolunteerTasks");

            migrationBuilder.DropIndex(
                name: "IX_VolunteerTasks_EventId_CategoryId",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "VolunteerTasks");

            migrationBuilder.AlterColumn<int>(
                name: "SubcategoryId",
                table: "VolunteerTasks",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerTasks_VolunteerSubcategories_SubcategoryId",
                table: "VolunteerTasks",
                column: "SubcategoryId",
                principalTable: "VolunteerSubcategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
