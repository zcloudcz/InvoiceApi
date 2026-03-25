using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class ExcludeDeletedFromDocumentNumberUniqueIndex : Migration
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
                filter: "\"DocumentNumber\" IS NOT NULL AND \"DocumentNumber\" <> 'DRAFT' AND \"Status\" <> 5");
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
                column: "DocumentNumber",
                unique: true,
                filter: "\"DocumentNumber\" IS NOT NULL AND \"DocumentNumber\" <> 'DRAFT'");
        }
    }
}
