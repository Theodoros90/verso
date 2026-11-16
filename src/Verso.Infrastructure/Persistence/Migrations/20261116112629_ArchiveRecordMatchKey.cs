using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveRecordMatchKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatchKey",
                schema: "documents",
                table: "ArchiveRecords",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAt",
                schema: "documents",
                table: "ArchiveRecords",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByDocumentId",
                schema: "documents",
                table: "ArchiveRecords",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveRecords_MatchKey",
                schema: "documents",
                table: "ArchiveRecords",
                column: "MatchKey",
                filter: "[MatchKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArchiveRecords_MatchKey",
                schema: "documents",
                table: "ArchiveRecords");

            migrationBuilder.DropColumn(
                name: "MatchKey",
                schema: "documents",
                table: "ArchiveRecords");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                schema: "documents",
                table: "ArchiveRecords");

            migrationBuilder.DropColumn(
                name: "SupersededByDocumentId",
                schema: "documents",
                table: "ArchiveRecords");
        }
    }
}
