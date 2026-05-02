using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddEpoTaxOfficeCode_v37 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add nullable EPO tax office code (c_ufo) to the Client table.
            // Only the issuer record (IsIssuer = true) uses this value.
            // Nullable — NULL means "not yet configured"; EPO export will raise
            // EpoValidationException until the user sets this in company settings.
            migrationBuilder.AddColumn<int>(
                name: "EpoTaxOfficeCode",
                table: "Client",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EpoTaxOfficeCode",
                table: "Client");
        }
    }
}
