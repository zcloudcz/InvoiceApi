using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <summary>
    /// Corrects the Prefix values on the default Proforma and TaxReceiptForAdvance number sequences.
    ///
    /// Previous values (from AddProformaDocumentType):
    ///   Id=3  Proforma              → "PF"
    ///   Id=4  TaxReceiptForAdvance  → "ZF"
    ///
    /// Required by issue #26 (Czech naming conventions):
    ///   Id=3  Proforma              → "PF-"   (zálohová faktura / proforma)
    ///   Id=4  TaxReceiptForAdvance  → "DPP-"  (daňový doklad o přijaté platbě)
    ///
    /// UPDATE is guarded with a WHERE clause so re-running is safe (idempotent).
    /// The Down() restores the original values in case a rollback is needed.
    ///
    /// Note: AddProformaDocumentType was also edited to seed the correct prefixes
    /// for fresh tenant DBs. This fix-up migration handles DBs that already ran
    /// AddProformaDocumentType with the old prefix values.
    /// </summary>
    public partial class FixProformaDppPrefixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Update Proforma prefix: "PF" → "PF-"
            // Only update if the row still has the old value — makes migration idempotent.
            migrationBuilder.Sql(
                "UPDATE \"NumberSequence\" SET \"Prefix\" = 'PF-' WHERE \"Id\" = 3 AND \"Prefix\" = 'PF'");

            // Update TaxReceiptForAdvance prefix: "ZF" → "DPP-"
            migrationBuilder.Sql(
                "UPDATE \"NumberSequence\" SET \"Prefix\" = 'DPP-' WHERE \"Id\" = 4 AND \"Prefix\" = 'ZF'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore original (pre-fix) values for rollback.
            migrationBuilder.Sql(
                "UPDATE \"NumberSequence\" SET \"Prefix\" = 'PF' WHERE \"Id\" = 3 AND \"Prefix\" = 'PF-'");

            migrationBuilder.Sql(
                "UPDATE \"NumberSequence\" SET \"Prefix\" = 'ZF' WHERE \"Id\" = 4 AND \"Prefix\" = 'DPP-'");
        }
    }
}
