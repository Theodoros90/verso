using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                schema: "documents",
                table: "ArchiveRecords",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            // Archive rows written before this migration (M7): file name, then each metadata value, one per line.
            migrationBuilder.Sql("""
                UPDATE a SET SearchText = d.FileName + COALESCE(CHAR(10) + (SELECT STRING_AGG(j.[value], CHAR(10)) FROM OPENJSON(a.Metadata) j), N'')
                FROM [documents].[ArchiveRecords] a
                JOIN [documents].[Documents] d ON d.Id = a.DocumentId
                WHERE a.SearchText = N''
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveRecords_ArchivedAt",
                schema: "documents",
                table: "ArchiveRecords",
                column: "ArchivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArchiveRecords_ArchivedAt",
                schema: "documents",
                table: "ArchiveRecords");

            migrationBuilder.DropColumn(
                name: "SearchText",
                schema: "documents",
                table: "ArchiveRecords");
        }
    }
}
