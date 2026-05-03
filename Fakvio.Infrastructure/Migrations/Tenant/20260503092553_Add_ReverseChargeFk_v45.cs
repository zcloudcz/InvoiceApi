using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class Add_ReverseChargeFk_v45 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // InformationalVatAmount: on-invoice VAT amount displayed for PDP items.
            // For ReverseCharge items the billed VAT is 0, but Czech law (§92a ZDPH) requires
            // the calculated VAT to appear on the document so the buyer can self-assess.
            // Default 0 = backward-compatible (all existing Standard items have no informational VAT).
            migrationBuilder.AddColumn<decimal>(
                name: "InformationalVatAmount",
                table: "InvoiceItem",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // ReverseChargeCodeId: nullable FK to ReverseChargeCode lookup table.
            // Required when VatRegime == ReverseCharge; NULL for all other regimes.
            migrationBuilder.AddColumn<long>(
                name: "ReverseChargeCodeId",
                table: "InvoiceItem",
                type: "bigint",
                nullable: true);

            // VatRegime: integer enum (0=Standard, 1=ReverseCharge, 2=Exempt, 3=OutOfScope).
            // Default 0 (Standard) makes the column backward-compatible — all existing rows
            // remain Standard regime without a data migration.
            migrationBuilder.AddColumn<int>(
                name: "VatRegime",
                table: "InvoiceItem",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItem_ReverseChargeCodeId",
                table: "InvoiceItem",
                column: "ReverseChargeCodeId");

            // FK to ReverseChargeCode: Restrict delete protects historical invoice data.
            // If a MFČR code is ever deactivated, the FK row must remain intact.
            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceItem_ReverseChargeCode_ReverseChargeCodeId",
                table: "InvoiceItem",
                column: "ReverseChargeCodeId",
                principalTable: "ReverseChargeCode",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceItem_ReverseChargeCode_ReverseChargeCodeId",
                table: "InvoiceItem");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceItem_ReverseChargeCodeId",
                table: "InvoiceItem");

            migrationBuilder.DropColumn(
                name: "InformationalVatAmount",
                table: "InvoiceItem");

            migrationBuilder.DropColumn(
                name: "ReverseChargeCodeId",
                table: "InvoiceItem");

            migrationBuilder.DropColumn(
                name: "VatRegime",
                table: "InvoiceItem");
        }
    }
}
