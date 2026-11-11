using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTypedFolderPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TypedFolderPaths",
                schema: "ingestion",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    AllowInput = table.Column<bool>(type: "bit", nullable: false),
                    AllowOutput = table.Column<bool>(type: "bit", nullable: false),
                    FirstRegisteredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FirstRegisteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TypedFolderPaths", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TypedFolderPaths",
                schema: "ingestion");
        }
    }
}
