using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddTwoFactorAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedTwoFactorAttempts",
                table: "User",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecretEncrypted",
                table: "User",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TwoFactorEmailCode",
                table: "User",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TwoFactorEmailCodeExpiresAt",
                table: "User",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TwoFactorEnabled",
                table: "User",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "TwoFactorEnabledAt",
                table: "User",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TwoFactorMethod",
                table: "User",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TwoFactorSessionToken",
                table: "User",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TwoFactorSessionTokenExpiresAt",
                table: "User",
                type: "datetime2",
                nullable: true);

            migrationBuilder.InsertData(
                table: "ContentTemplate",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "Description", "HtmlBody", "IsActive", "IsDefault", "Name", "Subject", "TemplateType", "UpdatedAt", "UpdatedByUserId" },
                values: new object[] { 7L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email sent with a 6-digit OTP code for Two-Factor Authentication.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Verification Code</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>Your two-factor authentication code is:</p><div style=\"text-align: center; margin: 30px 0;\"><span style=\"background-color: #f5f5f5; padding: 16px 32px; font-size: 32px; font-weight: bold; letter-spacing: 8px; border-radius: 8px; border: 2px solid #1976D2;\">{{Code}}</span></div><p style=\"color: #666; font-size: 14px;\">This code is valid for <strong>{{ExpirationMinutes}} minutes</strong>.</p><p style=\"color: #666; font-size: 14px;\">If you did not request this code, please ignore this email.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{AppName}}</p></div>", true, true, "Default Two-Factor Email", "Your verification code — {{AppName}}", 23, null, null });

            migrationBuilder.UpdateData(
                table: "User",
                keyColumn: "Id",
                keyValue: 1L,
                columns: new[] { "TotpSecretEncrypted", "TwoFactorEmailCode", "TwoFactorEmailCodeExpiresAt", "TwoFactorEnabledAt", "TwoFactorSessionToken", "TwoFactorSessionTokenExpiresAt" },
                values: new object[] { null, null, null, null, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_User_TwoFactorEnabled",
                table: "User",
                column: "TwoFactorEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_User_TwoFactorSessionToken",
                table: "User",
                column: "TwoFactorSessionToken",
                filter: "[TwoFactorSessionToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_User_TwoFactorEnabled",
                table: "User");

            migrationBuilder.DropIndex(
                name: "IX_User_TwoFactorSessionToken",
                table: "User");

            migrationBuilder.DeleteData(
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 7L);

            migrationBuilder.DropColumn(
                name: "FailedTwoFactorAttempts",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TotpSecretEncrypted",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TwoFactorEmailCode",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TwoFactorEmailCodeExpiresAt",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TwoFactorEnabled",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TwoFactorEnabledAt",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TwoFactorMethod",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TwoFactorSessionToken",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TwoFactorSessionTokenExpiresAt",
                table: "User");
        }
    }
}
