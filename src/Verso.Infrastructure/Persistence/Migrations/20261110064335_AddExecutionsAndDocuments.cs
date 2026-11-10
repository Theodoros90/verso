using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionsAndDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.EnsureSchema(
                name: "executions");

            migrationBuilder.CreateTable(
                name: "Documents",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TemplateVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    StorageProvider = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    StorageKey = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.CheckConstraint("CK_Documents_Archived", "([Status] = 'Generated' AND [ArchivedAt] IS NULL) OR ([Status] = 'Archived' AND [ArchivedAt] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "Executions",
                schema: "executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowKey = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowVersionNumber = table.Column<int>(type: "int", nullable: false),
                    ResolvedTemplateVersions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    InputPayload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Executions", x => x.Id);
                    table.CheckConstraint("CK_Executions_Completed", "([Status] IN ('Succeeded', 'Rejected', 'Failed') AND [CompletedAt] IS NOT NULL) OR ([Status] IN ('Queued', 'Running') AND [CompletedAt] IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "ArchiveRecords",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Retention = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    RetentionUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FolderPath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArchiveRecords_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "documents",
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionEvents",
                schema: "executions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Type = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Data = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionEvents_Executions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalSchema: "executions",
                        principalTable: "Executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionSteps",
                schema: "executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NodeType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OutputPort = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    Result = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionSteps_Executions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalSchema: "executions",
                        principalTable: "Executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ArchiveRecords_Document",
                schema: "documents",
                table: "ArchiveRecords",
                column: "DocumentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Documents_ExecutionNode",
                schema: "documents",
                table: "Documents",
                columns: new[] { "ExecutionId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionEvents_Execution",
                schema: "executions",
                table: "ExecutionEvents",
                columns: new[] { "ExecutionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_SubmittedAt",
                schema: "executions",
                table: "Executions",
                column: "SubmittedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Executions_Unfinished",
                schema: "executions",
                table: "Executions",
                column: "DispatchedAt",
                filter: "[Status] IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "UX_Executions_IdempotencyKey",
                schema: "executions",
                table: "Executions",
                columns: new[] { "WorkflowId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ExecutionSteps_Node",
                schema: "executions",
                table: "ExecutionSteps",
                columns: new[] { "ExecutionId", "NodeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveRecords",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "ExecutionEvents",
                schema: "executions");

            migrationBuilder.DropTable(
                name: "ExecutionSteps",
                schema: "executions");

            migrationBuilder.DropTable(
                name: "Documents",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "Executions",
                schema: "executions");
        }
    }
}
