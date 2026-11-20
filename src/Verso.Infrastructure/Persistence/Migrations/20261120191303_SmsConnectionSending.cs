using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verso.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SmsConnectionSending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowSending",
                schema: "connections",
                table: "SmsConnections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AllowedCountryPrefixes",
                schema: "connections",
                table: "SmsConnections",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowSending",
                schema: "connections",
                table: "SmsConnections");

            migrationBuilder.DropColumn(
                name: "AllowedCountryPrefixes",
                schema: "connections",
                table: "SmsConnections");
        }
    }
}
