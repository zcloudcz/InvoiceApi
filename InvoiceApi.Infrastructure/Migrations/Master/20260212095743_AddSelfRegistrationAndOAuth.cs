using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddSelfRegistrationAndOAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "User",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<string>(
                name: "EmailVerificationToken",
                table: "User",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerificationTokenExpiresAt",
                table: "User",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalProvider",
                table: "User",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExternalProviderId",
                table: "User",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEmailVerified",
                table: "User",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "User",
                keyColumn: "Id",
                keyValue: 1L,
                columns: new[] { "EmailVerificationToken", "EmailVerificationTokenExpiresAt", "ExternalProviderId", "IsEmailVerified" },
                values: new object[] { null, null, null, true });

            migrationBuilder.CreateIndex(
                name: "IX_User_EmailVerificationToken",
                table: "User",
                column: "EmailVerificationToken",
                filter: "[EmailVerificationToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_User_ExternalProvider_ExternalProviderId",
                table: "User",
                columns: new[] { "ExternalProvider", "ExternalProviderId" },
                unique: true,
                filter: "[ExternalProvider] <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_User_IsEmailVerified",
                table: "User",
                column: "IsEmailVerified");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_User_EmailVerificationToken",
                table: "User");

            migrationBuilder.DropIndex(
                name: "IX_User_ExternalProvider_ExternalProviderId",
                table: "User");

            migrationBuilder.DropIndex(
                name: "IX_User_IsEmailVerified",
                table: "User");

            migrationBuilder.DropColumn(
                name: "EmailVerificationToken",
                table: "User");

            migrationBuilder.DropColumn(
                name: "EmailVerificationTokenExpiresAt",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ExternalProvider",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ExternalProviderId",
                table: "User");

            migrationBuilder.DropColumn(
                name: "IsEmailVerified",
                table: "User");

            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "User",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
