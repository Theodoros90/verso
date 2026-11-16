using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                schema: "templates",
                table: "Templates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentCategories",
                schema: "templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentCategories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Templates_CategoryId",
                schema: "templates",
                table: "Templates",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "UX_DocumentCategories_Name",
                schema: "templates",
                table: "DocumentCategories",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Templates_DocumentCategories_CategoryId",
                schema: "templates",
                table: "Templates",
                column: "CategoryId",
                principalSchema: "templates",
                principalTable: "DocumentCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Templates_DocumentCategories_CategoryId",
                schema: "templates",
                table: "Templates");

            migrationBuilder.DropTable(
                name: "DocumentCategories",
                schema: "templates");

            migrationBuilder.DropIndex(
                name: "IX_Templates_CategoryId",
                schema: "templates",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                schema: "templates",
                table: "Templates");
        }
    }
}
