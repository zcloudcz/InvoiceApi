using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddCloudStorageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GoogleDriveAccessToken",
                table: "CompanySystemSettings",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GoogleDriveEnabled",
                table: "CompanySystemSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleDriveFolderId",
                table: "CompanySystemSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleDriveFolderName",
                table: "CompanySystemSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleDriveRefreshToken",
                table: "CompanySystemSettings",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GoogleDriveTokenExpiresAt",
                table: "CompanySystemSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OneDriveAccessToken",
                table: "CompanySystemSettings",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OneDriveEnabled",
                table: "CompanySystemSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OneDriveFolderId",
                table: "CompanySystemSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OneDriveFolderName",
                table: "CompanySystemSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OneDriveRefreshToken",
                table: "CompanySystemSettings",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OneDriveTokenExpiresAt",
                table: "CompanySystemSettings",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GoogleDriveAccessToken",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "GoogleDriveEnabled",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "GoogleDriveFolderId",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "GoogleDriveFolderName",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "GoogleDriveRefreshToken",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "GoogleDriveTokenExpiresAt",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "OneDriveAccessToken",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "OneDriveEnabled",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "OneDriveFolderId",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "OneDriveFolderName",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "OneDriveRefreshToken",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "OneDriveTokenExpiresAt",
                table: "CompanySystemSettings");
        }
    }
}
