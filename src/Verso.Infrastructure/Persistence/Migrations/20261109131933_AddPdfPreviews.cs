using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Verso.Infrastructure.Jobs;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPdfPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PdfPreviews",
                schema: "templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Layout = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Issues = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MarginBottomMm = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    MarginLeftMm = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    MarginRightMm = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    MarginTopMm = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    PageOrientation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PageSize = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PdfPreviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PdfPreviews_TemplateVersions_TemplateVersionId",
                        column: x => x.TemplateVersionId,
                        principalSchema: "templates",
                        principalTable: "TemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PdfPreviews_CreatedAt",
                schema: "templates",
                table: "PdfPreviews",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PdfPreviews_TemplateVersionId",
                schema: "templates",
                table: "PdfPreviews",
                column: "TemplateVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PdfPreviews_Unnotified",
                schema: "templates",
                table: "PdfPreviews",
                column: "CompletedAt",
                filter: "[NotifiedAt] IS NULL AND [CompletedAt] IS NOT NULL");

            // Hangfire job storage (schema "hangfire"), from Hangfire.SqlServer 1.8.25's own install script.
            // Created here so it is versioned with our schema; Hangfire never changes the schema at startup.
            migrationBuilder.Sql(HangfireJobs.InstallScript());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PdfPreviews",
                schema: "templates");

            // Removes the Hangfire job storage (all queued and historical jobs).
            migrationBuilder.Sql("""
                DECLARE @sql nvarchar(max) = N'';
                SELECT @sql += N'ALTER TABLE [hangfire].' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(f.name) + N';'
                  FROM sys.foreign_keys f JOIN sys.tables t ON f.parent_object_id = t.object_id
                 WHERE SCHEMA_NAME(t.schema_id) = N'hangfire';
                SELECT @sql += N'DROP TABLE [hangfire].' + QUOTENAME(name) + N';' FROM sys.tables WHERE SCHEMA_NAME(schema_id) = N'hangfire';
                EXEC sp_executesql @sql;
                IF SCHEMA_ID(N'hangfire') IS NOT NULL EXEC(N'DROP SCHEMA [hangfire]');
                """);
        }
    }
}
