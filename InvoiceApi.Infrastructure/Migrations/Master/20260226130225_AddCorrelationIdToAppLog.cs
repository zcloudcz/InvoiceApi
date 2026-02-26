using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddCorrelationIdToAppLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "AppLog",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppLog_CorrelationId",
                table: "AppLog",
                column: "CorrelationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppLog_CorrelationId",
                table: "AppLog");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "AppLog");
        }
    }
}
