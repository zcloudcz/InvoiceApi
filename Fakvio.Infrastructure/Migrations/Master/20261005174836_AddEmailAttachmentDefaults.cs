using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddEmailAttachmentDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmailAttachIsdoc",
                table: "BillingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EmailAttachPdf",
                table: "BillingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EmailAttachUbl",
                table: "BillingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailAttachIsdoc",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "EmailAttachPdf",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "EmailAttachUbl",
                table: "BillingSettings");
        }
    }
}
