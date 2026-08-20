using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class RemoveEmailVerificationToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Issue #162 — the email verification path was never wired up: nothing in the
            // production code ever wrote EmailVerificationToken, so both columns and the
            // lookup index were always empty. Ownership of the address is proven by the
            // emailed InvitationToken link instead (UserService.SetPasswordAsync sets
            // IsEmailVerified). Dropping dead columns is therefore data-loss free.
            migrationBuilder.DropIndex(
                name: "IX_User_EmailVerificationToken",
                table: "User");

            migrationBuilder.DropColumn(
                name: "EmailVerificationToken",
                table: "User");

            migrationBuilder.DropColumn(
                name: "EmailVerificationTokenExpiresAt",
                table: "User");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recreates the columns empty — there was never any data to restore.
            migrationBuilder.AddColumn<string>(
                name: "EmailVerificationToken",
                table: "User",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerificationTokenExpiresAt",
                table: "User",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "User",
                keyColumn: "Id",
                keyValue: 1L,
                columns: new[] { "EmailVerificationToken", "EmailVerificationTokenExpiresAt" },
                values: new object[] { null, null });

            migrationBuilder.CreateIndex(
                name: "IX_User_EmailVerificationToken",
                table: "User",
                column: "EmailVerificationToken",
                filter: "\"EmailVerificationToken\" IS NOT NULL");
        }
    }
}
