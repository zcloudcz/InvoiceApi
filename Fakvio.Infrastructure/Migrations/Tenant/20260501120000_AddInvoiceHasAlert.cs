using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddInvoiceHasAlert : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // HasAlert: a flag that marks a document as needing manual review.
            // Currently used by AdvanceTaxReceiptService to flag DPP documents issued
            // for overpayment amounts (paidAmount > proforma.TotalWithVat).
            // Issue #8 will expose this flag in the UI grid and Dashboard "Alerts" tile.
            migrationBuilder.AddColumn<bool>(
                name: "HasAlert",
                table: "Invoice",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasAlert",
                table: "Invoice");
        }
    }
}
