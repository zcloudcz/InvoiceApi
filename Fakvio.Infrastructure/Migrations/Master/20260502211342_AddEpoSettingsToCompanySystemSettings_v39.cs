using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddEpoSettingsToCompanySystemSettings_v39 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EpoAuthorizedPersonName",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EpoContactEmail",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EpoContactPhone",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EpoDefaultPeriodType",
                table: "CompanySystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EpoTaxOfficeBranchCode",
                table: "CompanySystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EpoTaxOfficeCode",
                table: "CompanySystemSettings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EpoAuthorizedPersonName",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "EpoContactEmail",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "EpoContactPhone",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "EpoDefaultPeriodType",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "EpoTaxOfficeBranchCode",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "EpoTaxOfficeCode",
                table: "CompanySystemSettings");
        }
    }
}
