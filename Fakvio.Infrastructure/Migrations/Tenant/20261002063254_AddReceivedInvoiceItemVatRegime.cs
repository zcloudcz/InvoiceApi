using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddReceivedInvoiceItemVatRegime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "InformationalVatAmount",
                table: "ReceivedInvoiceItem",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "ReverseChargeCodeId",
                table: "ReceivedInvoiceItem",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VatRegime",
                table: "ReceivedInvoiceItem",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoiceItem_ReverseChargeCodeId",
                table: "ReceivedInvoiceItem",
                column: "ReverseChargeCodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ReceivedInvoiceItem_ReverseChargeCode_ReverseChargeCodeId",
                table: "ReceivedInvoiceItem",
                column: "ReverseChargeCodeId",
                principalTable: "ReverseChargeCode",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReceivedInvoiceItem_ReverseChargeCode_ReverseChargeCodeId",
                table: "ReceivedInvoiceItem");

            migrationBuilder.DropIndex(
                name: "IX_ReceivedInvoiceItem_ReverseChargeCodeId",
                table: "ReceivedInvoiceItem");

            migrationBuilder.DropColumn(
                name: "InformationalVatAmount",
                table: "ReceivedInvoiceItem");

            migrationBuilder.DropColumn(
                name: "ReverseChargeCodeId",
                table: "ReceivedInvoiceItem");

            migrationBuilder.DropColumn(
                name: "VatRegime",
                table: "ReceivedInvoiceItem");
        }
    }
}
