using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFolderInput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ingestion");

            migrationBuilder.CreateTable(
                name: "FolderAccessTests",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Targets = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Results = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Identity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Machine = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderAccessTests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FolderFiles",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowKey = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    NodeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceLocation = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SourceSubfolder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    LastWriteAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedLocation = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ProcessedSubfolder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FailedLocation = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    FailedSubfolder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ErrorCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveredName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DeliveryError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ClaimedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderFiles", x => x.Id);
                    table.CheckConstraint("CK_FolderFiles_Completed", "([Status] = 'Completed' AND [CompletedAt] IS NOT NULL) OR ([Status] <> 'Completed' AND [CompletedAt] IS NULL)");
                    table.CheckConstraint("CK_FolderFiles_Submitted", "[Status] <> 'Submitted' OR [ExecutionId] IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "FolderLocations",
                schema: "ingestion",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AllowInput = table.Column<bool>(type: "bit", nullable: false),
                    AllowOutput = table.Column<bool>(type: "bit", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Problem = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReportedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderLocations", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "FolderWatches",
                schema: "ingestion",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowKey = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    NodeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastScanStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastScanAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastFilesFound = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderWatches", x => x.WorkflowId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessTests_RequestedAt",
                schema: "ingestion",
                table: "FolderAccessTests",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FolderFiles_Unfinished",
                schema: "ingestion",
                table: "FolderFiles",
                columns: new[] { "Status", "ClaimedAt" },
                filter: "[Status] <> 'Completed'");

            migrationBuilder.CreateIndex(
                name: "IX_FolderFiles_Workflow",
                schema: "ingestion",
                table: "FolderFiles",
                columns: new[] { "WorkflowId", "ClaimedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_FolderFiles_Claim",
                schema: "ingestion",
                table: "FolderFiles",
                columns: new[] { "SourceLocation", "SourceSubfolder", "RelativePath" },
                unique: true,
                filter: "[Status] = 'Claimed'");

            migrationBuilder.CreateIndex(
                name: "UX_FolderFiles_Execution",
                schema: "ingestion",
                table: "FolderFiles",
                column: "ExecutionId",
                unique: true,
                filter: "[ExecutionId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FolderAccessTests",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "FolderFiles",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "FolderLocations",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "FolderWatches",
                schema: "ingestion");
        }
    }
}
