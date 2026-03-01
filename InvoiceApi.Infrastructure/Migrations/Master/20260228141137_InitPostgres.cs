using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace InvoiceApi.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class InitPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Exception = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    CompanyId = table.Column<long>(type: "bigint", nullable: true),
                    RequestPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AresCache",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegistrationNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JsonData = table.Column<string>(type: "text", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsSuccessful = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CompanyName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TaxNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsVatPayer = table.Column<bool>(type: "boolean", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AresCache", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContentTemplate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    TemplateType = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DecimalPlaces = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    DisplayFormat = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FormatPattern = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CounterDigits = table.Column<int>(type: "integer", nullable: false),
                    ResetsYearly = table.Column<bool>(type: "boolean", nullable: false),
                    ResetsMonthly = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSequenceFormat", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemConfiguration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SmtpHost = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpUsername = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SmtpPassword = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SmtpSenderEmail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SmtpSenderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    JwtExpirationHours = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemConfiguration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VatRate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsReduced = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegistrationNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TaxNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CompanyName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TradingName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsVatPayer = table.Column<bool>(type: "boolean", nullable: false),
                    IsIssuer = table.Column<bool>(type: "boolean", nullable: false),
                    LastAresFetchDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    PreferredCurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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
                name: "Address",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    AddressType = table.Column<int>(type: "integer", nullable: false),
                    Street = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    City = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AddressLine2 = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Address", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Address_Client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BankAccount",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BankName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AccountNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IBAN = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SWIFT = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccount", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankAccount_Client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BillingSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    DueDateCalculationType = table.Column<int>(type: "integer", nullable: false),
                    DueDays = table.Column<int>(type: "integer", nullable: false),
                    CustomInvoiceNumberSequenceId = table.Column<long>(type: "bigint", nullable: true),
                    CustomCreditNoteNumberSequenceId = table.Column<long>(type: "bigint", nullable: true),
                    InvoiceNumberPrefix = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    InvoiceNumberSuffix = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreditNoteNumberPrefix = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreditNoteNumberSuffix = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DefaultPaymentMethod = table.Column<int>(type: "integer", nullable: true),
                    BankAccountNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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

            migrationBuilder.CreateTable(
                name: "CompanySystemSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    SchemaName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    ProvisionedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsProvisioned = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    MaxUsers = table.Column<int>(type: "integer", nullable: true),
                    AdminNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SmtpHost = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: true),
                    SmtpUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SmtpPassword = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SmtpSenderEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SmtpSenderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SmtpUseSsl = table.Column<bool>(type: "boolean", nullable: true),
                    GoogleDriveEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    GoogleDriveAccessToken = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    GoogleDriveRefreshToken = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    GoogleDriveTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GoogleDriveFolderId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    GoogleDriveFolderName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OneDriveEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    OneDriveAccessToken = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OneDriveRefreshToken = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OneDriveTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OneDriveFolderId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OneDriveFolderName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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
                name: "Contact",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    ContactType = table.Column<int>(type: "integer", nullable: false),
                    ContactValue = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contact", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contact_Client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "User",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InvitationToken = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InvitationTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsInvitationPending = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ExternalProvider = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ExternalProviderId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsEmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    EmailVerificationToken = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EmailVerificationTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    TwoFactorMethod = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TotpSecretEncrypted = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TwoFactorEnabledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TwoFactorEmailCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TwoFactorEmailCodeExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TwoFactorSessionToken = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TwoFactorSessionTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedTwoFactorAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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
                    { 1L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Default HTML template for rendering invoice PDFs.", "<!DOCTYPE html>\r\n<html>\r\n<head>\r\n<meta charset=\"utf-8\" />\r\n<style>\r\n    body {\r\n        font-family: Arial, Helvetica, sans-serif;\r\n        font-size: 11px;\r\n        color: #333;\r\n        margin: 25px 30px;\r\n        line-height: 1.4;\r\n    }\r\n\r\n    /* ── Supplier (DODAVATEL) section ── */\r\n    table.issuer-block {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-bottom: 15px;\r\n    }\r\n    table.issuer-block td {\r\n        vertical-align: top;\r\n        padding: 2px 0;\r\n    }\r\n    .issuer-label {\r\n        font-size: 10px;\r\n        color: #777;\r\n        text-transform: uppercase;\r\n        letter-spacing: 0.5px;\r\n        padding-bottom: 4px;\r\n    }\r\n    .issuer-name {\r\n        font-size: 13px;\r\n        font-weight: bold;\r\n        color: #333;\r\n    }\r\n    .issuer-detail {\r\n        color: #555;\r\n        font-size: 11px;\r\n    }\r\n\r\n    /* ── Blue header bar (document type + number) ── */\r\n    table.doc-header {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin: 12px 0 15px 0;\r\n    }\r\n    .doc-header-bar {\r\n        background-color: #5B7D9D;\r\n        color: #fff;\r\n        padding: 8px 12px;\r\n        font-size: 12px;\r\n    }\r\n    .doc-header-bar .doc-type {\r\n        font-size: 14px;\r\n        font-weight: bold;\r\n        text-transform: uppercase;\r\n    }\r\n    .doc-header-bar .doc-subtitle {\r\n        font-size: 10px;\r\n        font-weight: bold;\r\n        text-transform: uppercase;\r\n    }\r\n    .doc-number-cell {\r\n        background-color: #5B7D9D;\r\n        color: #fff;\r\n        text-align: right;\r\n        padding: 8px 12px;\r\n        font-size: 22px;\r\n        font-weight: bold;\r\n        letter-spacing: 1px;\r\n    }\r\n\r\n    /* ── Client (ODBĚRATEL) + dates section ── */\r\n    table.client-dates {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-bottom: 20px;\r\n    }\r\n    table.client-dates td {\r\n        vertical-align: top;\r\n        padding: 2px 0;\r\n    }\r\n    .client-label {\r\n        font-size: 10px;\r\n        color: #777;\r\n        text-transform: uppercase;\r\n        letter-spacing: 0.5px;\r\n        padding-bottom: 4px;\r\n    }\r\n    .client-name {\r\n        font-weight: bold;\r\n        font-size: 12px;\r\n    }\r\n    .date-label {\r\n        color: #777;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        padding: 3px 10px 3px 0;\r\n    }\r\n    .date-value {\r\n        text-align: right;\r\n        font-weight: bold;\r\n        padding: 3px 0;\r\n    }\r\n\r\n    /* ── Items table ── */\r\n    table.items {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin: 5px 0 15px 0;\r\n    }\r\n    table.items th {\r\n        background-color: #5B7D9D;\r\n        color: #fff;\r\n        padding: 6px 8px;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        font-weight: bold;\r\n        text-align: left;\r\n    }\r\n    table.items td {\r\n        padding: 6px 8px;\r\n        border-bottom: 1px solid #e0e0e0;\r\n        font-size: 11px;\r\n    }\r\n\r\n    /* ── VAT breakdown + grand total ── */\r\n    table.vat-summary {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-top: 10px;\r\n    }\r\n    table.vat-summary th {\r\n        background-color: #e8e8e8;\r\n        padding: 5px 10px;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        font-weight: bold;\r\n        text-align: center;\r\n    }\r\n    table.vat-summary td {\r\n        padding: 4px 10px;\r\n        font-size: 11px;\r\n    }\r\n    .grand-total-row {\r\n        background-color: #f0f0f0;\r\n    }\r\n    .grand-total-row td {\r\n        font-weight: bold;\r\n        font-size: 13px;\r\n        padding: 8px 10px;\r\n        text-transform: uppercase;\r\n    }\r\n\r\n    /* ── Payment info + QR code ── */\r\n    table.payment-info {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-top: 20px;\r\n    }\r\n    table.payment-info td {\r\n        vertical-align: top;\r\n        padding: 2px 5px;\r\n    }\r\n    .payment-label {\r\n        color: #777;\r\n        font-size: 10px;\r\n    }\r\n\r\n    /* ── Separator line ── */\r\n    .separator {\r\n        border: none;\r\n        border-top: 1px solid #ccc;\r\n        margin: 10px 0;\r\n    }\r\n</style>\r\n</head>\r\n<body>\r\n\r\n<!-- ═══════ ISSUER (DODAVATEL) SECTION ═══════ -->\r\n<table class=\"issuer-block\">\r\n    <tr>\r\n        <td style=\"width:50%\">\r\n            <div class=\"issuer-label\">DODAVATEL</div>\r\n            <div class=\"issuer-name\">{{IssuerName}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerStreet}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerPostalCode}} {{IssuerCity}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerCountry}}</div>\r\n        </td>\r\n        <td style=\"width:50%; text-align:right\">\r\n            <table style=\"margin-left:auto; border-collapse:collapse;\">\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">Tel:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerPhone}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">Email:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerEmail}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">IČ:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerRegistrationNumber}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">DIČ:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerTaxNumber}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">B.Ú.:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{BankAccountNumber}}</td></tr>\r\n            </table>\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ DOCUMENT HEADER BAR ═══════ -->\r\n<table class=\"doc-header\">\r\n    <tr>\r\n        <td class=\"doc-header-bar\" style=\"width:60%\">\r\n            <span class=\"doc-type\">{{DocumentTypeLabel}}</span>\r\n            <span style=\"font-size:11px; font-weight:normal;\">(variabilní symbol)</span>\r\n            <br/>\r\n            <span class=\"doc-subtitle\">DAŇOVÝ DOKLAD</span>\r\n        </td>\r\n        <td class=\"doc-number-cell\">{{VariableSymbol}}</td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ CLIENT (ODBĚRATEL) + DATES ═══════ -->\r\n<table class=\"client-dates\">\r\n    <tr>\r\n        <td style=\"width:50%\">\r\n            <div class=\"client-label\">ODBĚRATEL</div>\r\n            <div class=\"client-name\">{{ClientName}}</div>\r\n            <div class=\"issuer-detail\">{{ClientStreet}}</div>\r\n            <div class=\"issuer-detail\">{{ClientPostalCode}} {{ClientCity}}</div>\r\n            <div class=\"issuer-detail\">IČ: {{ClientRegistrationNumber}}</div>\r\n            <div class=\"issuer-detail\">DIČ: {{ClientTaxNumber}}</div>\r\n        </td>\r\n        <td style=\"width:50%; vertical-align:top; padding-left:30px;\">\r\n            <table style=\"width:100%; border-collapse:collapse;\">\r\n                <tr><td class=\"date-label\">DATUM VYSTAVENÍ</td><td class=\"date-value\">{{IssueDate}}</td></tr>\r\n                <tr><td class=\"date-label\">DATUM SPLATNOSTI</td><td class=\"date-value\">{{DueDate}}</td></tr>\r\n                <tr><td class=\"date-label\">DATUM ZDAN. PLNĚNÍ</td><td class=\"date-value\">{{TaxableSupplyDate}}</td></tr>\r\n                <tr><td class=\"date-label\">FORMA ÚHRADY</td><td class=\"date-value\">{{PaymentMethod}}</td></tr>\r\n            </table>\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n<hr class=\"separator\" />\r\n\r\n<!-- ═══════ INVOICE ITEMS TABLE ═══════ -->\r\n<table class=\"items\">\r\n    <thead>\r\n        <tr>\r\n            <th style=\"width:40%\">POPIS POLOŽKY</th>\r\n            <th style=\"text-align:center; width:8%\">MJ</th>\r\n            <th style=\"text-align:center; width:8%\">DPH</th>\r\n            <th style=\"text-align:center; width:8%\">POČET</th>\r\n            <th style=\"text-align:right; width:18%\">CENA/MJ</th>\r\n            <th style=\"text-align:right; width:18%\">CELKEM BEZ DPH</th>\r\n        </tr>\r\n    </thead>\r\n    <tbody>\r\n        {{InvoiceItems}}\r\n    </tbody>\r\n</table>\r\n\r\n<!-- ═══════ VAT BREAKDOWN + GRAND TOTAL ═══════ -->\r\n<table class=\"vat-summary\">\r\n    <thead>\r\n        <tr>\r\n            <th style=\"width:34%\">SAZBA</th>\r\n            <th style=\"width:33%; text-align:right\">ZÁKLAD</th>\r\n            <th style=\"width:33%; text-align:right\">DPH</th>\r\n        </tr>\r\n    </thead>\r\n    <tbody>\r\n        {{VatBreakdown}}\r\n    </tbody>\r\n</table>\r\n\r\n<table class=\"vat-summary\" style=\"margin-top:0\">\r\n    <tr class=\"grand-total-row\">\r\n        <td style=\"width:67%\">CELKEM K ÚHRADĚ</td>\r\n        <td style=\"width:33%; text-align:right\">{{TotalWithVat}} {{CurrencySymbol}}</td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ QR CODE (if available) ═══════ -->\r\n<table class=\"payment-info\">\r\n    <tr>\r\n        <td style=\"text-align:right\">\r\n            {{QrCodeImage}}\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n</body>\r\n</html>", true, true, "Default Invoice PDF", null, 1, null, null },
                    { 2L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Default HTML template for rendering credit note PDFs.", "<!DOCTYPE html>\r\n<html>\r\n<head>\r\n<meta charset=\"utf-8\" />\r\n<style>\r\n    body {\r\n        font-family: Arial, Helvetica, sans-serif;\r\n        font-size: 11px;\r\n        color: #333;\r\n        margin: 25px 30px;\r\n        line-height: 1.4;\r\n    }\r\n\r\n    /* ── Supplier (DODAVATEL) section ── */\r\n    table.issuer-block {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-bottom: 15px;\r\n    }\r\n    table.issuer-block td {\r\n        vertical-align: top;\r\n        padding: 2px 0;\r\n    }\r\n    .issuer-label {\r\n        font-size: 10px;\r\n        color: #777;\r\n        text-transform: uppercase;\r\n        letter-spacing: 0.5px;\r\n        padding-bottom: 4px;\r\n    }\r\n    .issuer-name {\r\n        font-size: 13px;\r\n        font-weight: bold;\r\n        color: #333;\r\n    }\r\n    .issuer-detail {\r\n        color: #555;\r\n        font-size: 11px;\r\n    }\r\n\r\n    /* ── Blue header bar (document type + number) ── */\r\n    table.doc-header {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin: 12px 0 15px 0;\r\n    }\r\n    .doc-header-bar {\r\n        background-color: #A05050;\r\n        color: #fff;\r\n        padding: 8px 12px;\r\n        font-size: 12px;\r\n    }\r\n    .doc-header-bar .doc-type {\r\n        font-size: 14px;\r\n        font-weight: bold;\r\n        text-transform: uppercase;\r\n    }\r\n    .doc-header-bar .doc-subtitle {\r\n        font-size: 10px;\r\n        font-weight: bold;\r\n        text-transform: uppercase;\r\n    }\r\n    .doc-number-cell {\r\n        background-color: #A05050;\r\n        color: #fff;\r\n        text-align: right;\r\n        padding: 8px 12px;\r\n        font-size: 22px;\r\n        font-weight: bold;\r\n        letter-spacing: 1px;\r\n    }\r\n\r\n    /* ── Client (ODBĚRATEL) + dates section ── */\r\n    table.client-dates {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-bottom: 20px;\r\n    }\r\n    table.client-dates td {\r\n        vertical-align: top;\r\n        padding: 2px 0;\r\n    }\r\n    .client-label {\r\n        font-size: 10px;\r\n        color: #777;\r\n        text-transform: uppercase;\r\n        letter-spacing: 0.5px;\r\n        padding-bottom: 4px;\r\n    }\r\n    .client-name {\r\n        font-weight: bold;\r\n        font-size: 12px;\r\n    }\r\n    .date-label {\r\n        color: #777;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        padding: 3px 10px 3px 0;\r\n    }\r\n    .date-value {\r\n        text-align: right;\r\n        font-weight: bold;\r\n        padding: 3px 0;\r\n    }\r\n\r\n    /* ── Items table ── */\r\n    table.items {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin: 5px 0 15px 0;\r\n    }\r\n    table.items th {\r\n        background-color: #A05050;\r\n        color: #fff;\r\n        padding: 6px 8px;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        font-weight: bold;\r\n        text-align: left;\r\n    }\r\n    table.items td {\r\n        padding: 6px 8px;\r\n        border-bottom: 1px solid #e0e0e0;\r\n        font-size: 11px;\r\n    }\r\n\r\n    /* ── VAT breakdown + grand total ── */\r\n    table.vat-summary {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-top: 10px;\r\n    }\r\n    table.vat-summary th {\r\n        background-color: #e8e8e8;\r\n        padding: 5px 10px;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        font-weight: bold;\r\n        text-align: center;\r\n    }\r\n    table.vat-summary td {\r\n        padding: 4px 10px;\r\n        font-size: 11px;\r\n    }\r\n    .grand-total-row {\r\n        background-color: #f0f0f0;\r\n    }\r\n    .grand-total-row td {\r\n        font-weight: bold;\r\n        font-size: 13px;\r\n        padding: 8px 10px;\r\n        text-transform: uppercase;\r\n    }\r\n\r\n    /* ── Payment info + QR code ── */\r\n    table.payment-info {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-top: 20px;\r\n    }\r\n    table.payment-info td {\r\n        vertical-align: top;\r\n        padding: 2px 5px;\r\n    }\r\n    .payment-label {\r\n        color: #777;\r\n        font-size: 10px;\r\n    }\r\n\r\n    /* ── Separator line ── */\r\n    .separator {\r\n        border: none;\r\n        border-top: 1px solid #ccc;\r\n        margin: 10px 0;\r\n    }\r\n</style>\r\n</head>\r\n<body>\r\n\r\n<!-- ═══════ ISSUER (DODAVATEL) SECTION ═══════ -->\r\n<table class=\"issuer-block\">\r\n    <tr>\r\n        <td style=\"width:50%\">\r\n            <div class=\"issuer-label\">DODAVATEL</div>\r\n            <div class=\"issuer-name\">{{IssuerName}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerStreet}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerPostalCode}} {{IssuerCity}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerCountry}}</div>\r\n        </td>\r\n        <td style=\"width:50%; text-align:right\">\r\n            <table style=\"margin-left:auto; border-collapse:collapse;\">\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">Tel:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerPhone}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">Email:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerEmail}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">IČ:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerRegistrationNumber}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">DIČ:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerTaxNumber}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">B.Ú.:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{BankAccountNumber}}</td></tr>\r\n            </table>\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ DOCUMENT HEADER BAR ═══════ -->\r\n<table class=\"doc-header\">\r\n    <tr>\r\n        <td class=\"doc-header-bar\" style=\"width:60%\">\r\n            <span class=\"doc-type\">{{DocumentTypeLabel}}</span>\r\n            <span style=\"font-size:11px; font-weight:normal;\">(variabilní symbol)</span>\r\n            <br/>\r\n            <span class=\"doc-subtitle\">DAŇOVÝ DOKLAD</span>\r\n        </td>\r\n        <td class=\"doc-number-cell\">{{VariableSymbol}}</td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ CLIENT (ODBĚRATEL) + DATES ═══════ -->\r\n<table class=\"client-dates\">\r\n    <tr>\r\n        <td style=\"width:50%\">\r\n            <div class=\"client-label\">ODBĚRATEL</div>\r\n            <div class=\"client-name\">{{ClientName}}</div>\r\n            <div class=\"issuer-detail\">{{ClientStreet}}</div>\r\n            <div class=\"issuer-detail\">{{ClientPostalCode}} {{ClientCity}}</div>\r\n            <div class=\"issuer-detail\">IČ: {{ClientRegistrationNumber}}</div>\r\n            <div class=\"issuer-detail\">DIČ: {{ClientTaxNumber}}</div>\r\n        </td>\r\n        <td style=\"width:50%; vertical-align:top; padding-left:30px;\">\r\n            <table style=\"width:100%; border-collapse:collapse;\">\r\n                <tr><td class=\"date-label\">DATUM VYSTAVENÍ</td><td class=\"date-value\">{{IssueDate}}</td></tr>\r\n                <tr><td class=\"date-label\">DATUM SPLATNOSTI</td><td class=\"date-value\">{{DueDate}}</td></tr>\r\n                <tr><td class=\"date-label\">DATUM ZDAN. PLNĚNÍ</td><td class=\"date-value\">{{TaxableSupplyDate}}</td></tr>\r\n                <tr><td class=\"date-label\">FORMA ÚHRADY</td><td class=\"date-value\">{{PaymentMethod}}</td></tr>\r\n            </table>\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n<hr class=\"separator\" />\r\n\r\n<!-- ═══════ INVOICE ITEMS TABLE ═══════ -->\r\n<table class=\"items\">\r\n    <thead>\r\n        <tr>\r\n            <th style=\"width:40%\">POPIS POLOŽKY</th>\r\n            <th style=\"text-align:center; width:8%\">MJ</th>\r\n            <th style=\"text-align:center; width:8%\">DPH</th>\r\n            <th style=\"text-align:center; width:8%\">POČET</th>\r\n            <th style=\"text-align:right; width:18%\">CENA/MJ</th>\r\n            <th style=\"text-align:right; width:18%\">CELKEM BEZ DPH</th>\r\n        </tr>\r\n    </thead>\r\n    <tbody>\r\n        {{InvoiceItems}}\r\n    </tbody>\r\n</table>\r\n\r\n<!-- ═══════ VAT BREAKDOWN + GRAND TOTAL ═══════ -->\r\n<table class=\"vat-summary\">\r\n    <thead>\r\n        <tr>\r\n            <th style=\"width:34%\">SAZBA</th>\r\n            <th style=\"width:33%; text-align:right\">ZÁKLAD</th>\r\n            <th style=\"width:33%; text-align:right\">DPH</th>\r\n        </tr>\r\n    </thead>\r\n    <tbody>\r\n        {{VatBreakdown}}\r\n    </tbody>\r\n</table>\r\n\r\n<table class=\"vat-summary\" style=\"margin-top:0\">\r\n    <tr class=\"grand-total-row\">\r\n        <td style=\"width:67%\">CELKEM K ÚHRADĚ</td>\r\n        <td style=\"width:33%; text-align:right\">{{TotalWithVat}} {{CurrencySymbol}}</td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ QR CODE (if available) ═══════ -->\r\n<table class=\"payment-info\">\r\n    <tr>\r\n        <td style=\"text-align:right\">\r\n            {{QrCodeImage}}\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n</body>\r\n</html>", true, true, "Default Credit Note PDF", null, 2, null, null },
                    { 3L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email body when sending an invoice.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Invoice {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached invoice <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><br/><p>Thank you for your business.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{CompanyName}}</p></div>", true, true, "Default Invoice Email", "Invoice {{InvoiceNumber}} from {{CompanyName}}", 10, null, null },
                    { 4L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email body when sending a credit note.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Credit Note {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached credit note <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Thank you for your business.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{CompanyName}}</p></div>", true, true, "Default Credit Note Email", "Credit Note {{InvoiceNumber}} from {{CompanyName}}", 11, null, null },
                    { 5L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email sent to new users.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Welcome to {{AppName}}</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>You have been invited to {{AppName}}. Please set your password by clicking the button below:</p><div style=\"text-align: center; margin: 30px 0;\"><a href=\"{{InvitationLink}}\" style=\"background-color: #1976D2; color: white; padding: 14px 28px; text-decoration: none; border-radius: 4px; font-size: 16px;\">Set Password</a></div><p style=\"color: #666; font-size: 14px;\">This link is valid for 48 hours.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{AppName}}</p></div>", true, true, "Default Invitation Email", "Invitation to {{AppName}} — Set your password", 20, null, null },
                    { 6L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Payment reminder for overdue invoices.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #E65100;\">Payment Reminder</h2><p>Dear customer,</p><p>This is a friendly reminder that invoice <strong>{{InvoiceNumber}}</strong> is overdue.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><p>Please arrange payment at your earliest convenience.</p><br/><p>Thank you.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{CompanyName}}</p></div>", true, true, "Default Payment Reminder", "Payment reminder — Invoice {{InvoiceNumber}}", 21, null, null },
                    { 7L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email sent with a 6-digit OTP code for Two-Factor Authentication.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Verification Code</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>Your two-factor authentication code is:</p><div style=\"text-align: center; margin: 30px 0;\"><span style=\"background-color: #f5f5f5; padding: 16px 32px; font-size: 32px; font-weight: bold; letter-spacing: 8px; border-radius: 8px; border: 2px solid #1976D2;\">{{Code}}</span></div><p style=\"color: #666; font-size: 14px;\">This code is valid for <strong>{{ExpirationMinutes}} minutes</strong>.</p><p style=\"color: #666; font-size: 14px;\">If you did not request this code, please ignore this email.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{AppName}}</p></div>", true, true, "Default Two-Factor Email", "Your verification code — {{AppName}}", 23, null, null }
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
                    { 5L, "PLN", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "{0:N2} zł", true, "Polish Złoty", 5, "zł", null, null },
                    { 6L, "CHF", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "CHF {0:N2}", true, "Swiss Franc", 6, "CHF", null, null },
                    { 7L, "HUF", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 0, "{0:N0} Ft", true, "Hungarian Forint", 7, "Ft", null, null },
                    { 8L, "RON", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "{0:N2} lei", true, "Romanian Leu", 8, "lei", null, null }
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
                columns: new[] { "Id", "CompanyId", "CreatedAt", "CreatedByUserId", "Email", "EmailVerificationToken", "EmailVerificationTokenExpiresAt", "ExternalProviderId", "FirstName", "InvitationToken", "InvitationTokenExpiresAt", "IsActive", "IsEmailVerified", "LastLoginAt", "LastName", "PasswordHash", "Role", "TotpSecretEncrypted", "TwoFactorEmailCode", "TwoFactorEmailCodeExpiresAt", "TwoFactorEnabledAt", "TwoFactorSessionToken", "TwoFactorSessionTokenExpiresAt", "UpdatedAt", "UpdatedByUserId" },
                values: new object[] { 1L, null, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "admin@zcloud.cz", null, null, null, "System", null, null, true, true, null, "Administrator", "$2a$12$aI/Mx3cUBwheuL1U1laUee1OLR92DaWxdu3SLMauc5zWy7VoVwEAu", 2, null, null, null, null, null, null, null, null });

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
                name: "IX_Address_AddressType",
                table: "Address",
                column: "AddressType");

            migrationBuilder.CreateIndex(
                name: "IX_Address_ClientId",
                table: "Address",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_AppLog_CorrelationId",
                table: "AppLog",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AppLog_Level",
                table: "AppLog",
                column: "Level");

            migrationBuilder.CreateIndex(
                name: "IX_AppLog_Level_Timestamp",
                table: "AppLog",
                columns: new[] { "Level", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AppLog_Timestamp",
                table: "AppLog",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AresCache_ExpiresAt",
                table: "AresCache",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AresCache_RegistrationNumber",
                table: "AresCache",
                column: "RegistrationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankAccount_ClientId",
                table: "BankAccount",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_BankAccount_ClientId_IsDefault",
                table: "BankAccount",
                columns: new[] { "ClientId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingSettings_ClientId",
                table: "BillingSettings",
                column: "ClientId",
                unique: true);

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
                name: "IX_Contact_ClientId",
                table: "Contact",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_ContactType",
                table: "Contact",
                column: "ContactType");

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
                name: "IX_User_EmailVerificationToken",
                table: "User",
                column: "EmailVerificationToken",
                filter: "\"EmailVerificationToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_User_ExternalProvider_ExternalProviderId",
                table: "User",
                columns: new[] { "ExternalProvider", "ExternalProviderId" },
                unique: true,
                filter: "\"ExternalProvider\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_User_InvitationToken",
                table: "User",
                column: "InvitationToken",
                unique: true,
                filter: "\"InvitationToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_User_IsActive",
                table: "User",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_User_IsEmailVerified",
                table: "User",
                column: "IsEmailVerified");

            migrationBuilder.CreateIndex(
                name: "IX_User_Role",
                table: "User",
                column: "Role");

            migrationBuilder.CreateIndex(
                name: "IX_User_TwoFactorEnabled",
                table: "User",
                column: "TwoFactorEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_User_TwoFactorSessionToken",
                table: "User",
                column: "TwoFactorSessionToken",
                filter: "\"TwoFactorSessionToken\" IS NOT NULL");

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
                name: "Address");

            migrationBuilder.DropTable(
                name: "AppLog");

            migrationBuilder.DropTable(
                name: "AresCache");

            migrationBuilder.DropTable(
                name: "BankAccount");

            migrationBuilder.DropTable(
                name: "BillingSettings");

            migrationBuilder.DropTable(
                name: "CompanySystemSettings");

            migrationBuilder.DropTable(
                name: "Contact");

            migrationBuilder.DropTable(
                name: "ContentTemplate");

            migrationBuilder.DropTable(
                name: "NumberSequenceFormat");

            migrationBuilder.DropTable(
                name: "SystemConfiguration");

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
