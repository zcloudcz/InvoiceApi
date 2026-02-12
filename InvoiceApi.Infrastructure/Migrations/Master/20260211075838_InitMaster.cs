using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace InvoiceApi.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class InitMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentTemplate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TemplateType = table.Column<int>(type: "int", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentTemplate", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Currency",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DecimalPlaces = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    DisplayFormat = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Currency", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberSequenceFormat",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FormatPattern = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CounterDigits = table.Column<int>(type: "int", nullable: false),
                    ResetsYearly = table.Column<bool>(type: "bit", nullable: false),
                    ResetsMonthly = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSequenceFormat", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VatRate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsReduced = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatRate", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Client",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistrationNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TaxNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TradingName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsVatPayer = table.Column<bool>(type: "bit", nullable: false),
                    IsIssuer = table.Column<bool>(type: "bit", nullable: false),
                    LastAresFetchDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PreferredCurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Client", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Client_Currency_PreferredCurrencyId",
                        column: x => x.PreferredCurrencyId,
                        principalTable: "Currency",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanySystemSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ConnectionString = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProvisionedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsProvisioned = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MaxUsers = table.Column<int>(type: "int", nullable: true),
                    AdminNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanySystemSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanySystemSettings_Client_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "User",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InvitationToken = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InvitationTokenExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsInvitationPending = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                    table.ForeignKey(
                        name: "FK_User_Client_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "ContentTemplate",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "Description", "HtmlBody", "IsActive", "IsDefault", "Name", "Subject", "TemplateType", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { 1L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Default HTML template for rendering invoice PDFs.", "<!DOCTYPE html><html><head><meta charset=\"utf-8\" /><style>body { font-family: Arial, sans-serif; font-size: 12px; color: #333; margin: 30px; } h1 { color: #2c3e50; margin-bottom: 5px; } .header { display: flex; justify-content: space-between; margin-bottom: 30px; } .company-info { margin-bottom: 20px; } .company-info h3 { margin-bottom: 5px; color: #2c3e50; } .invoice-meta { background: #f8f9fa; padding: 15px; border-radius: 5px; margin-bottom: 20px; } .invoice-meta table td { padding: 3px 10px; } table.items { width: 100%; border-collapse: collapse; margin: 20px 0; } table.items th { background: #2c3e50; color: white; padding: 8px; text-align: left; } table.items td { padding: 8px; border-bottom: 1px solid #ddd; } table.items tr:nth-child(even) { background: #f8f9fa; } .totals { float: right; margin-top: 10px; } .totals table td { padding: 5px 15px; } .totals .grand-total { font-size: 16px; font-weight: bold; color: #2c3e50; border-top: 2px solid #2c3e50; } .payment-info { clear: both; margin-top: 40px; padding: 15px; background: #f8f9fa; border-radius: 5px; } .payment-info h3 { margin-bottom: 10px; } .notes { margin-top: 20px; padding: 10px; border-left: 3px solid #2c3e50; } .footer { margin-top: 40px; text-align: center; font-size: 10px; color: #999; }</style></head><body><h1>{{DocumentType}} {{DocumentNumber}}</h1><div class=\"header\"><div class=\"company-info\"><h3>Dodavatel / Supplier</h3><strong>{{IssuerName}}</strong><br/>{{IssuerStreet}}<br/>{{IssuerPostalCode}} {{IssuerCity}}<br/>{{IssuerCountry}}<br/>IČO: {{IssuerRegistrationNumber}}<br/>DIČ: {{IssuerTaxNumber}}</div><div class=\"company-info\"><h3>Odběratel / Client</h3><strong>{{ClientName}}</strong><br/>{{ClientStreet}}<br/>{{ClientPostalCode}} {{ClientCity}}<br/>{{ClientCountry}}<br/>IČO: {{ClientRegistrationNumber}}<br/>DIČ: {{ClientTaxNumber}}</div></div><div class=\"invoice-meta\"><table><tr><td><strong>Datum vystavení:</strong></td><td>{{IssueDate}}</td></tr><tr><td><strong>Datum splatnosti:</strong></td><td>{{DueDate}}</td></tr><tr><td><strong>DÚZP:</strong></td><td>{{TaxableSupplyDate}}</td></tr><tr><td><strong>Způsob platby:</strong></td><td>{{PaymentMethod}}</td></tr></table></div><table class=\"items\"><thead><tr><th>#</th><th>Popis</th><th style=\"text-align:right\">Množství</th><th>Jednotka</th><th style=\"text-align:right\">Cena/ks</th><th style=\"text-align:right\">DPH</th><th style=\"text-align:right\">Bez DPH</th><th style=\"text-align:right\">DPH</th><th style=\"text-align:right\">Celkem</th></tr></thead><tbody>{{InvoiceItems}}</tbody></table><div class=\"totals\"><table><tr><td>Celkem bez DPH:</td><td style=\"text-align:right\">{{TotalBeforeVat}} {{CurrencyCode}}</td></tr><tr><td>DPH:</td><td style=\"text-align:right\">{{TotalVat}} {{CurrencyCode}}</td></tr><tr class=\"grand-total\"><td>Celkem s DPH:</td><td style=\"text-align:right\">{{TotalWithVat}} {{CurrencyCode}}</td></tr></table></div><div class=\"payment-info\"><h3>Platební údaje</h3><table><tr><td><strong>Číslo účtu:</strong></td><td>{{BankAccountNumber}}</td></tr><tr><td><strong>IBAN:</strong></td><td>{{IBAN}}</td></tr><tr><td><strong>SWIFT:</strong></td><td>{{SWIFT}}</td></tr><tr><td><strong>Variabilní symbol:</strong></td><td>{{VariableSymbol}}</td></tr><tr><td><strong>Konstantní symbol:</strong></td><td>{{ConstantSymbol}}</td></tr></table></div><div class=\"notes\"><strong>Poznámky:</strong><br/>{{Notes}}</div><div class=\"footer\">Generated by InvoiceApi</div></body></html>", true, true, "Default Invoice PDF", null, 1, null, null },
                    { 2L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Default HTML template for rendering credit note PDFs.", "<!DOCTYPE html><html><head><meta charset=\"utf-8\" /><style>body { font-family: Arial, sans-serif; font-size: 12px; color: #333; margin: 30px; } h1 { color: #2c3e50; margin-bottom: 5px; } .header { display: flex; justify-content: space-between; margin-bottom: 30px; } .company-info { margin-bottom: 20px; } .company-info h3 { margin-bottom: 5px; color: #2c3e50; } .invoice-meta { background: #f8f9fa; padding: 15px; border-radius: 5px; margin-bottom: 20px; } .invoice-meta table td { padding: 3px 10px; } table.items { width: 100%; border-collapse: collapse; margin: 20px 0; } table.items th { background: #2c3e50; color: white; padding: 8px; text-align: left; } table.items td { padding: 8px; border-bottom: 1px solid #ddd; } table.items tr:nth-child(even) { background: #f8f9fa; } .totals { float: right; margin-top: 10px; } .totals table td { padding: 5px 15px; } .totals .grand-total { font-size: 16px; font-weight: bold; color: #2c3e50; border-top: 2px solid #2c3e50; } .payment-info { clear: both; margin-top: 40px; padding: 15px; background: #f8f9fa; border-radius: 5px; } .payment-info h3 { margin-bottom: 10px; } .notes { margin-top: 20px; padding: 10px; border-left: 3px solid #2c3e50; } .footer { margin-top: 40px; text-align: center; font-size: 10px; color: #999; }</style></head><body><h1>{{DocumentType}} {{DocumentNumber}}</h1><div class=\"header\"><div class=\"company-info\"><h3>Dodavatel / Supplier</h3><strong>{{IssuerName}}</strong><br/>{{IssuerStreet}}<br/>{{IssuerPostalCode}} {{IssuerCity}}<br/>{{IssuerCountry}}<br/>IČO: {{IssuerRegistrationNumber}}<br/>DIČ: {{IssuerTaxNumber}}</div><div class=\"company-info\"><h3>Odběratel / Client</h3><strong>{{ClientName}}</strong><br/>{{ClientStreet}}<br/>{{ClientPostalCode}} {{ClientCity}}<br/>{{ClientCountry}}<br/>IČO: {{ClientRegistrationNumber}}<br/>DIČ: {{ClientTaxNumber}}</div></div><div class=\"invoice-meta\"><table><tr><td><strong>Datum vystavení:</strong></td><td>{{IssueDate}}</td></tr><tr><td><strong>Datum splatnosti:</strong></td><td>{{DueDate}}</td></tr><tr><td><strong>DÚZP:</strong></td><td>{{TaxableSupplyDate}}</td></tr><tr><td><strong>Způsob platby:</strong></td><td>{{PaymentMethod}}</td></tr></table></div><table class=\"items\"><thead><tr><th>#</th><th>Popis</th><th style=\"text-align:right\">Množství</th><th>Jednotka</th><th style=\"text-align:right\">Cena/ks</th><th style=\"text-align:right\">DPH</th><th style=\"text-align:right\">Bez DPH</th><th style=\"text-align:right\">DPH</th><th style=\"text-align:right\">Celkem</th></tr></thead><tbody>{{InvoiceItems}}</tbody></table><div class=\"totals\"><table><tr><td>Celkem bez DPH:</td><td style=\"text-align:right\">{{TotalBeforeVat}} {{CurrencyCode}}</td></tr><tr><td>DPH:</td><td style=\"text-align:right\">{{TotalVat}} {{CurrencyCode}}</td></tr><tr class=\"grand-total\"><td>Celkem s DPH:</td><td style=\"text-align:right\">{{TotalWithVat}} {{CurrencyCode}}</td></tr></table></div><div class=\"payment-info\"><h3>Platební údaje</h3><table><tr><td><strong>Číslo účtu:</strong></td><td>{{BankAccountNumber}}</td></tr><tr><td><strong>IBAN:</strong></td><td>{{IBAN}}</td></tr><tr><td><strong>SWIFT:</strong></td><td>{{SWIFT}}</td></tr><tr><td><strong>Variabilní symbol:</strong></td><td>{{VariableSymbol}}</td></tr><tr><td><strong>Konstantní symbol:</strong></td><td>{{ConstantSymbol}}</td></tr></table></div><div class=\"notes\"><strong>Poznámky:</strong><br/>{{Notes}}</div><div class=\"footer\">Generated by InvoiceApi</div></body></html>", true, true, "Default Credit Note PDF", null, 2, null, null },
                    { 3L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email body when sending an invoice.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Invoice {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached invoice <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><br/><p>Thank you for your business.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{CompanyName}}</p></div>", true, true, "Default Invoice Email", "Invoice {{InvoiceNumber}} from {{CompanyName}}", 10, null, null },
                    { 4L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email body when sending a credit note.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Credit Note {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached credit note <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Thank you for your business.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{CompanyName}}</p></div>", true, true, "Default Credit Note Email", "Credit Note {{InvoiceNumber}} from {{CompanyName}}", 11, null, null },
                    { 5L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email sent to new users.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Welcome to {{AppName}}</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>You have been invited to {{AppName}}. Please set your password by clicking the button below:</p><div style=\"text-align: center; margin: 30px 0;\"><a href=\"{{InvitationLink}}\" style=\"background-color: #1976D2; color: white; padding: 14px 28px; text-decoration: none; border-radius: 4px; font-size: 16px;\">Set Password</a></div><p style=\"color: #666; font-size: 14px;\">This link is valid for 48 hours.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{AppName}}</p></div>", true, true, "Default Invitation Email", "Invitation to {{AppName}} — Set your password", 20, null, null },
                    { 6L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Payment reminder for overdue invoices.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #E65100;\">Payment Reminder</h2><p>Dear customer,</p><p>This is a friendly reminder that invoice <strong>{{InvoiceNumber}}</strong> is overdue.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><p>Please arrange payment at your earliest convenience.</p><br/><p>Thank you.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{CompanyName}}</p></div>", true, true, "Default Payment Reminder", "Payment reminder — Invoice {{InvoiceNumber}}", 21, null, null }
                });

            migrationBuilder.InsertData(
                table: "Currency",
                columns: new[] { "Id", "Code", "CreatedAt", "CreatedByUserId", "DecimalPlaces", "DisplayFormat", "IsActive", "Name", "SortOrder", "Symbol", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { 1L, "CZK", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "{0:N2} Kč", true, "Česká koruna", 1, "Kč", null, null },
                    { 2L, "EUR", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "€{0:N2}", true, "Euro", 2, "€", null, null },
                    { 3L, "USD", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "${0:N2}", true, "US Dollar", 3, "$", null, null },
                    { 4L, "GBP", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "£{0:N2}", true, "British Pound", 4, "£", null, null },
                    { 5L, "PLN", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "{0:N2} zł", true, "Polish Złoty", 5, "zł", null, null }
                });

            migrationBuilder.InsertData(
                table: "NumberSequenceFormat",
                columns: new[] { "Id", "CounterDigits", "CreatedAt", "CreatedByUserId", "FormatPattern", "IsActive", "Name", "ResetsMonthly", "ResetsYearly", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { 1L, 3, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "yyyyNNN", true, "Standard yearly format (yyyyNNN)", false, true, null, null },
                    { 2L, 3, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "yyNNN", true, "Short yearly format (yyNNN)", false, true, null, null },
                    { 3L, 3, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "yyMMNNN", true, "Monthly format (yyMMNNN)", true, true, null, null },
                    { 4L, 6, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "NNNNNN", true, "Continuous format (NNNNNN)", false, false, null, null }
                });

            migrationBuilder.InsertData(
                table: "User",
                columns: new[] { "Id", "CompanyId", "CreatedAt", "CreatedByUserId", "Email", "FirstName", "InvitationToken", "InvitationTokenExpiresAt", "IsActive", "LastLoginAt", "LastName", "PasswordHash", "Role", "UpdatedAt", "UpdatedByUserId" },
                values: new object[] { 1L, null, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "admin@zcloud.cz", "System", null, null, true, null, "Administrator", "$2a$12$aI/Mx3cUBwheuL1U1laUee1OLR92DaWxdu3SLMauc5zWy7VoVwEAu", 2, null, null });

            migrationBuilder.InsertData(
                table: "VatRate",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "IsActive", "IsDefault", "IsReduced", "Name", "Rate", "UpdatedAt", "UpdatedByUserId", "ValidFrom", "ValidTo" },
                values: new object[,]
                {
                    { 1L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, true, false, "DPH 21% - standardní sazba", 21.00m, null, null, new DateTime(2013, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 2L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, true, true, "DPH 12% - snížená sazba", 12.00m, null, null, new DateTime(2015, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 3L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, false, false, "DPH 0% - osvobozeno od daně", 0.00m, null, null, new DateTime(2013, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Client_IsActive",
                table: "Client",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Client_IsIssuer",
                table: "Client",
                column: "IsIssuer");

            migrationBuilder.CreateIndex(
                name: "IX_Client_PreferredCurrencyId",
                table: "Client",
                column: "PreferredCurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Client_RegistrationNumber",
                table: "Client",
                column: "RegistrationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanySystemSettings_CompanyId",
                table: "CompanySystemSettings",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanySystemSettings_IsActive",
                table: "CompanySystemSettings",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CompanySystemSettings_IsProvisioned",
                table: "CompanySystemSettings",
                column: "IsProvisioned");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_IsActive",
                table: "ContentTemplate",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_IsDefault",
                table: "ContentTemplate",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_TemplateType",
                table: "ContentTemplate",
                column: "TemplateType");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_TemplateType_IsDefault",
                table: "ContentTemplate",
                columns: new[] { "TemplateType", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_Currency_Code",
                table: "Currency",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Currency_IsActive",
                table: "Currency",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Currency_SortOrder",
                table: "Currency",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_NumberSequenceFormat_IsActive",
                table: "NumberSequenceFormat",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_User_CompanyId",
                table: "User",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_User_Email",
                table: "User",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_User_InvitationToken",
                table: "User",
                column: "InvitationToken",
                unique: true,
                filter: "[InvitationToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_User_IsActive",
                table: "User",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_User_Role",
                table: "User",
                column: "Role");

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_IsActive",
                table: "VatRate",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_IsDefault",
                table: "VatRate",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_IsDefault_IsReduced",
                table: "VatRate",
                columns: new[] { "IsDefault", "IsReduced" });

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_ValidFrom",
                table: "VatRate",
                column: "ValidFrom");

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_ValidTo",
                table: "VatRate",
                column: "ValidTo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanySystemSettings");

            migrationBuilder.DropTable(
                name: "ContentTemplate");

            migrationBuilder.DropTable(
                name: "NumberSequenceFormat");

            migrationBuilder.DropTable(
                name: "User");

            migrationBuilder.DropTable(
                name: "VatRate");

            migrationBuilder.DropTable(
                name: "Client");

            migrationBuilder.DropTable(
                name: "Currency");
        }
    }
}
