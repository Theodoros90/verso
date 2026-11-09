using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates one SQL schema per module (templates, workflows, executions, documents, audit).
    /// Hand-written: EF only emits schemas once entities use them, and no entities exist yet.
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var schema in Schemas.All)
            {
                migrationBuilder.EnsureSchema(schema);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var schema in Schemas.All.Reverse())
            {
                migrationBuilder.DropSchema(schema);
            }
        }
    }
}
