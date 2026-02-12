using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddBillingSettingsAndSmtpToMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SmtpHost",
                table: "CompanySystemSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpPassword",
                table: "CompanySystemSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SmtpPort",
                table: "CompanySystemSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpSenderEmail",
                table: "CompanySystemSettings",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpSenderName",
                table: "CompanySystemSettings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SmtpUseSsl",
                table: "CompanySystemSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpUsername",
                table: "CompanySystemSettings",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BillingSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    DueDateCalculationType = table.Column<int>(type: "int", nullable: false),
                    DueDays = table.Column<int>(type: "int", nullable: false),
                    CustomInvoiceNumberSequenceId = table.Column<long>(type: "bigint", nullable: true),
                    CustomCreditNoteNumberSequenceId = table.Column<long>(type: "bigint", nullable: true),
                    InvoiceNumberPrefix = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    InvoiceNumberSuffix = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreditNoteNumberPrefix = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreditNoteNumberSuffix = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DefaultPaymentMethod = table.Column<int>(type: "int", nullable: true),
                    BankAccountNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingSettings_Client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingSettings_ClientId",
                table: "BillingSettings",
                column: "ClientId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "SmtpHost",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "SmtpPassword",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "SmtpPort",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "SmtpSenderEmail",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "SmtpSenderName",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "SmtpUseSsl",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "SmtpUsername",
                table: "CompanySystemSettings");
        }
    }
}
