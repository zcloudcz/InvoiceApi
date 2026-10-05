using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddDocumentFileNameFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreditNoteFileNameFormat",
                table: "BillingSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceFileNameFormat",
                table: "BillingSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProformaFileNameFormat",
                table: "BillingSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxReceiptFileNameFormat",
                table: "BillingSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreditNoteFileNameFormat",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "InvoiceFileNameFormat",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "ProformaFileNameFormat",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "TaxReceiptFileNameFormat",
                table: "BillingSettings");
        }
    }
}
