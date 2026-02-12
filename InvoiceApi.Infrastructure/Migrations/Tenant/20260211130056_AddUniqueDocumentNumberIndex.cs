using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddUniqueDocumentNumberIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoice_DocumentNumber",
                table: "Invoice");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_DocumentNumber",
                table: "Invoice",
                column: "DocumentNumber",
                unique: true,
                filter: "[DocumentNumber] IS NOT NULL AND [DocumentNumber] <> 'DRAFT'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoice_DocumentNumber",
                table: "Invoice");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_DocumentNumber",
                table: "Invoice",
                column: "DocumentNumber");
        }
    }
}
