using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveRecordCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                schema: "documents",
                table: "ArchiveRecords",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveRecords_CategoryId",
                schema: "documents",
                table: "ArchiveRecords",
                column: "CategoryId");

            // One-off backfill (ADR 0013 addendum 1): records archived before this migration take their template's
            // category as it is now. Documents without a template (HTML emails) stay uncategorised.
            migrationBuilder.Sql("""
                UPDATE a SET a.CategoryId = t.CategoryId
                FROM documents.ArchiveRecords a
                JOIN documents.Documents d ON d.Id = a.DocumentId
                JOIN templates.TemplateVersions v ON v.Id = d.TemplateVersionId
                JOIN templates.Templates t ON t.Id = v.TemplateId
                WHERE a.CategoryId IS NULL AND t.CategoryId IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArchiveRecords_CategoryId",
                schema: "documents",
                table: "ArchiveRecords");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                schema: "documents",
                table: "ArchiveRecords");
        }
    }
}
