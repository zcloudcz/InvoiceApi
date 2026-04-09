using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddBlobStorageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AzureBlobConnectionString",
                table: "SystemConfiguration",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureBlobContainerPrefix",
                table: "SystemConfiguration",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureBlobConnectionString",
                table: "CompanySystemSettings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureBlobContainerPrefix",
                table: "CompanySystemSettings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AzureBlobConnectionString",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AzureBlobContainerPrefix",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AzureBlobConnectionString",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "AzureBlobContainerPrefix",
                table: "CompanySystemSettings");
        }
    }
}
