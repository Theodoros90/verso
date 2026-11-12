using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopIterations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ExecutionSteps_Node",
                schema: "executions",
                table: "ExecutionSteps");

            migrationBuilder.DropIndex(
                name: "UX_Documents_ExecutionNode",
                schema: "documents",
                table: "Documents");

            migrationBuilder.AddColumn<int>(
                name: "Iteration",
                schema: "executions",
                table: "ExecutionSteps",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Iteration",
                schema: "documents",
                table: "Documents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "UX_ExecutionSteps_Node",
                schema: "executions",
                table: "ExecutionSteps",
                columns: new[] { "ExecutionId", "NodeId", "Iteration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Documents_ExecutionNode",
                schema: "documents",
                table: "Documents",
                columns: new[] { "ExecutionId", "NodeId", "Iteration" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ExecutionSteps_Node",
                schema: "executions",
                table: "ExecutionSteps");

            migrationBuilder.DropIndex(
                name: "UX_Documents_ExecutionNode",
                schema: "documents",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "Iteration",
                schema: "executions",
                table: "ExecutionSteps");

            migrationBuilder.DropColumn(
                name: "Iteration",
                schema: "documents",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "UX_ExecutionSteps_Node",
                schema: "executions",
                table: "ExecutionSteps",
                columns: new[] { "ExecutionId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Documents_ExecutionNode",
                schema: "documents",
                table: "Documents",
                columns: new[] { "ExecutionId", "NodeId" },
                unique: true);
        }
    }
}
