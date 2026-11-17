using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiConnections",
                schema: "connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_CI_AS"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TestPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AuthType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AuthName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TokenUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SecretProtected = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiConnectionTests",
                schema: "connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    ElapsedMs = table.Column<int>(type: "int", nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ResponseBytes = table.Column<long>(type: "bigint", nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiConnectionTests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ApiConnections_Key",
                schema: "connections",
                table: "ApiConnections",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiConnectionTests_RequestedAt",
                schema: "connections",
                table: "ApiConnectionTests",
                column: "RequestedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiConnections",
                schema: "connections");

            migrationBuilder.DropTable(
                name: "ApiConnectionTests",
                schema: "connections");
        }
    }
}
