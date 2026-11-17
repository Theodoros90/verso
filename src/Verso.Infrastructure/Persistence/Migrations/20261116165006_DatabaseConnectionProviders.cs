using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseConnectionProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Port",
                schema: "connections",
                table: "DatabaseConnections",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "connections",
                table: "DatabaseConnections",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "SqlServer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Port",
                schema: "connections",
                table: "DatabaseConnections");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "connections",
                table: "DatabaseConnections");
        }
    }
}
