using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SmsConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SmsConnections",
                schema: "connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_CI_AS"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Sender = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MessageType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    UnicodeEnabled = table.Column<bool>(type: "bit", nullable: false),
                    OrganisationPrefix = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SecretProtected = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SmsConnectionTests",
                schema: "connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RecipientProtected = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    ElapsedMs = table.Column<int>(type: "int", nullable: true),
                    MessageId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SmsCount = table.Column<int>(type: "int", nullable: true),
                    RemainingCredits = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsConnectionTests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_SmsConnections_Key",
                schema: "connections",
                table: "SmsConnections",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SmsConnectionTests_RequestedAt",
                schema: "connections",
                table: "SmsConnectionTests",
                column: "RequestedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmsConnections",
                schema: "connections");

            migrationBuilder.DropTable(
                name: "SmsConnectionTests",
                schema: "connections");
        }
    }
}
