using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddAdvanceTaxReceiptMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the new column with a default of 1 (OnPaymentMatch).
            // OnPaymentMatch matches the historical implicit behaviour — auto-convert when
            // a bank payment is matched via the IMAP pipeline. This ensures existing tenants
            // continue to work exactly as before without requiring manual reconfiguration.
            migrationBuilder.AddColumn<int>(
                name: "AdvanceTaxReceiptMode",
                table: "BillingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdvanceTaxReceiptMode",
                table: "BillingSettings");
        }
    }
}
