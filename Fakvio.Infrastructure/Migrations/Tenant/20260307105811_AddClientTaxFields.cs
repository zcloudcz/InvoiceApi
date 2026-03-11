using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddClientTaxFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivityType",
                schema: "tenant_template",
                table: "Client",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FlatRateBand",
                schema: "tenant_template",
                table: "Client",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMainActivity",
                schema: "tenant_template",
                table: "Client",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TaxRegime",
                schema: "tenant_template",
                table: "Client",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityType",
                schema: "tenant_template",
                table: "Client");

            migrationBuilder.DropColumn(
                name: "FlatRateBand",
                schema: "tenant_template",
                table: "Client");

            migrationBuilder.DropColumn(
                name: "IsMainActivity",
                schema: "tenant_template",
                table: "Client");

            migrationBuilder.DropColumn(
                name: "TaxRegime",
                schema: "tenant_template",
                table: "Client");
        }
    }
}
