using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalWaiting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Executions_Completed",
                schema: "executions",
                table: "Executions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Executions_Completed",
                schema: "executions",
                table: "Executions",
                sql: "([Status] IN ('Succeeded', 'Rejected', 'Failed') AND [CompletedAt] IS NOT NULL) OR ([Status] IN ('Queued', 'Running', 'AwaitingApproval') AND [CompletedAt] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Executions_Completed",
                schema: "executions",
                table: "Executions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Executions_Completed",
                schema: "executions",
                table: "Executions",
                sql: "([Status] IN ('Succeeded', 'Rejected', 'Failed') AND [CompletedAt] IS NOT NULL) OR ([Status] IN ('Queued', 'Running') AND [CompletedAt] IS NULL)");
        }
    }
}
