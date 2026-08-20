using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <summary>
    /// Adds BillingSettings.AdvanceTaxReceiptMode (enum EAdvanceTaxReceiptMode, stored as int):
    /// when a paid proforma is auto-converted into a tax receipt for advance payment.
    ///
    /// defaultValue is 1 (= OnPaymentMatch), NOT the CLR default 0 (= Disabled):
    /// existing tenants must land on the same value that the entity uses for new rows,
    /// otherwise half the tenants would silently end up with the feature switched off.
    /// This mirrors the C# initializer on BillingSettings.AdvanceTaxReceiptMode.
    ///
    /// The table name is intentionally unqualified — tenant migrations run against the
    /// tenant's own schema via search_path, so hardcoding a schema would break provisioning.
    /// </summary>
    public partial class AddAdvanceTaxReceiptMode_v55 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
