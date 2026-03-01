using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class InitPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tenant_template");

            migrationBuilder.CreateTable(
                name: "AresCache",
                schema: "tenant_template",
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
                schema: "tenant_template",
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
                schema: "tenant_template",
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
                schema: "tenant_template",
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
                name: "VatRate",
                schema: "tenant_template",
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
                schema: "tenant_template",
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
                        principalSchema: "tenant_template",
                        principalTable: "Currency",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NumberSequence",
                schema: "tenant_template",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DocumentType = table.Column<int>(type: "integer", nullable: false),
                    Prefix = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Suffix = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CurrentNumber = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    NumberSequenceFormatId = table.Column<long>(type: "bigint", nullable: false),
                    CurrentYear = table.Column<int>(type: "integer", nullable: true),
                    CurrentMonth = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSequence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NumberSequence_NumberSequenceFormat_NumberSequenceFormatId",
                        column: x => x.NumberSequenceFormatId,
                        principalSchema: "tenant_template",
                        principalTable: "NumberSequenceFormat",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Address",
                schema: "tenant_template",
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
                        principalSchema: "tenant_template",
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BankAccount",
                schema: "tenant_template",
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
                        principalSchema: "tenant_template",
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Contact",
                schema: "tenant_template",
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
                        principalSchema: "tenant_template",
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BillingSettings",
                schema: "tenant_template",
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
                        principalSchema: "tenant_template",
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BillingSettings_NumberSequence_CustomCreditNoteNumberSequen~",
                        column: x => x.CustomCreditNoteNumberSequenceId,
                        principalSchema: "tenant_template",
                        principalTable: "NumberSequence",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BillingSettings_NumberSequence_CustomInvoiceNumberSequenceId",
                        column: x => x.CustomInvoiceNumberSequenceId,
                        principalSchema: "tenant_template",
                        principalTable: "NumberSequence",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Invoice",
                schema: "tenant_template",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IssueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TaxableSupplyDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClientId = table.Column<long>(type: "bigint", nullable: true),
                    IssuerId = table.Column<long>(type: "bigint", nullable: false),
                    OriginalInvoiceId = table.Column<long>(type: "bigint", nullable: true),
                    VariableSymbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ConstantSymbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SpecificSymbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    BankAccountNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IBAN = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SWIFT = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PaymentMethod = table.Column<int>(type: "integer", nullable: true),
                    TotalBeforeVat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalVat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalWithVat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    Notes = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    IsExported = table.Column<bool>(type: "boolean", nullable: false),
                    LastExportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsSentByEmail = table.Column<bool>(type: "boolean", nullable: false),
                    LastSentByEmailAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InvoiceType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DueDateOffsetDays = table.Column<int>(type: "integer", nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UsageCount = table.Column<int>(type: "integer", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: true),
                    NumberSequenceId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invoice_Client_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenant_template",
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoice_Client_IssuerId",
                        column: x => x.IssuerId,
                        principalSchema: "tenant_template",
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoice_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalSchema: "tenant_template",
                        principalTable: "Currency",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoice_Invoice_OriginalInvoiceId",
                        column: x => x.OriginalInvoiceId,
                        principalSchema: "tenant_template",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoice_NumberSequence_NumberSequenceId",
                        column: x => x.NumberSequenceId,
                        principalSchema: "tenant_template",
                        principalTable: "NumberSequence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceItem",
                schema: "tenant_template",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<long>(type: "bigint", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatRateId = table.Column<long>(type: "bigint", nullable: true),
                    VatRatePercentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    TotalBeforeVat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalWithVat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceItem_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "tenant_template",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceItem_VatRate_VatRateId",
                        column: x => x.VatRateId,
                        principalSchema: "tenant_template",
                        principalTable: "VatRate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "tenant_template",
                table: "ContentTemplate",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "Description", "HtmlBody", "IsActive", "IsDefault", "Name", "Subject", "TemplateType", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { 1L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Default HTML template for rendering invoice PDFs.", "<!DOCTYPE html>\r\n<html>\r\n<head>\r\n<meta charset=\"utf-8\" />\r\n<style>\r\n    body {\r\n        font-family: Arial, Helvetica, sans-serif;\r\n        font-size: 11px;\r\n        color: #333;\r\n        margin: 25px 30px;\r\n        line-height: 1.4;\r\n    }\r\n\r\n    /* ── Supplier (DODAVATEL) section ── */\r\n    table.issuer-block {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-bottom: 15px;\r\n    }\r\n    table.issuer-block td {\r\n        vertical-align: top;\r\n        padding: 2px 0;\r\n    }\r\n    .issuer-label {\r\n        font-size: 10px;\r\n        color: #777;\r\n        text-transform: uppercase;\r\n        letter-spacing: 0.5px;\r\n        padding-bottom: 4px;\r\n    }\r\n    .issuer-name {\r\n        font-size: 13px;\r\n        font-weight: bold;\r\n        color: #333;\r\n    }\r\n    .issuer-detail {\r\n        color: #555;\r\n        font-size: 11px;\r\n    }\r\n\r\n    /* ── Blue header bar (document type + number) ── */\r\n    table.doc-header {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin: 12px 0 15px 0;\r\n    }\r\n    .doc-header-bar {\r\n        background-color: #5B7D9D;\r\n        color: #fff;\r\n        padding: 8px 12px;\r\n        font-size: 12px;\r\n    }\r\n    .doc-header-bar .doc-type {\r\n        font-size: 14px;\r\n        font-weight: bold;\r\n        text-transform: uppercase;\r\n    }\r\n    .doc-header-bar .doc-subtitle {\r\n        font-size: 10px;\r\n        font-weight: bold;\r\n        text-transform: uppercase;\r\n    }\r\n    .doc-number-cell {\r\n        background-color: #5B7D9D;\r\n        color: #fff;\r\n        text-align: right;\r\n        padding: 8px 12px;\r\n        font-size: 22px;\r\n        font-weight: bold;\r\n        letter-spacing: 1px;\r\n    }\r\n\r\n    /* ── Client (ODBĚRATEL) + dates section ── */\r\n    table.client-dates {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-bottom: 20px;\r\n    }\r\n    table.client-dates td {\r\n        vertical-align: top;\r\n        padding: 2px 0;\r\n    }\r\n    .client-label {\r\n        font-size: 10px;\r\n        color: #777;\r\n        text-transform: uppercase;\r\n        letter-spacing: 0.5px;\r\n        padding-bottom: 4px;\r\n    }\r\n    .client-name {\r\n        font-weight: bold;\r\n        font-size: 12px;\r\n    }\r\n    .date-label {\r\n        color: #777;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        padding: 3px 10px 3px 0;\r\n    }\r\n    .date-value {\r\n        text-align: right;\r\n        font-weight: bold;\r\n        padding: 3px 0;\r\n    }\r\n\r\n    /* ── Items table ── */\r\n    table.items {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin: 5px 0 15px 0;\r\n    }\r\n    table.items th {\r\n        background-color: #5B7D9D;\r\n        color: #fff;\r\n        padding: 6px 8px;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        font-weight: bold;\r\n        text-align: left;\r\n    }\r\n    table.items td {\r\n        padding: 6px 8px;\r\n        border-bottom: 1px solid #e0e0e0;\r\n        font-size: 11px;\r\n    }\r\n\r\n    /* ── VAT breakdown + grand total ── */\r\n    table.vat-summary {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-top: 10px;\r\n    }\r\n    table.vat-summary th {\r\n        background-color: #e8e8e8;\r\n        padding: 5px 10px;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        font-weight: bold;\r\n        text-align: center;\r\n    }\r\n    table.vat-summary td {\r\n        padding: 4px 10px;\r\n        font-size: 11px;\r\n    }\r\n    .grand-total-row {\r\n        background-color: #f0f0f0;\r\n    }\r\n    .grand-total-row td {\r\n        font-weight: bold;\r\n        font-size: 13px;\r\n        padding: 8px 10px;\r\n        text-transform: uppercase;\r\n    }\r\n\r\n    /* ── Payment info + QR code ── */\r\n    table.payment-info {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-top: 20px;\r\n    }\r\n    table.payment-info td {\r\n        vertical-align: top;\r\n        padding: 2px 5px;\r\n    }\r\n    .payment-label {\r\n        color: #777;\r\n        font-size: 10px;\r\n    }\r\n\r\n    /* ── Separator line ── */\r\n    .separator {\r\n        border: none;\r\n        border-top: 1px solid #ccc;\r\n        margin: 10px 0;\r\n    }\r\n</style>\r\n</head>\r\n<body>\r\n\r\n<!-- ═══════ ISSUER (DODAVATEL) SECTION ═══════ -->\r\n<table class=\"issuer-block\">\r\n    <tr>\r\n        <td style=\"width:50%\">\r\n            <div class=\"issuer-label\">DODAVATEL</div>\r\n            <div class=\"issuer-name\">{{IssuerName}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerStreet}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerPostalCode}} {{IssuerCity}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerCountry}}</div>\r\n        </td>\r\n        <td style=\"width:50%; text-align:right\">\r\n            <table style=\"margin-left:auto; border-collapse:collapse;\">\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">Tel:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerPhone}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">Email:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerEmail}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">IČ:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerRegistrationNumber}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">DIČ:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerTaxNumber}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">B.Ú.:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{BankAccountNumber}}</td></tr>\r\n            </table>\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ DOCUMENT HEADER BAR ═══════ -->\r\n<table class=\"doc-header\">\r\n    <tr>\r\n        <td class=\"doc-header-bar\" style=\"width:60%\">\r\n            <span class=\"doc-type\">{{DocumentTypeLabel}}</span>\r\n            <span style=\"font-size:11px; font-weight:normal;\">(variabilní symbol)</span>\r\n            <br/>\r\n            <span class=\"doc-subtitle\">DAŇOVÝ DOKLAD</span>\r\n        </td>\r\n        <td class=\"doc-number-cell\">{{VariableSymbol}}</td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ CLIENT (ODBĚRATEL) + DATES ═══════ -->\r\n<table class=\"client-dates\">\r\n    <tr>\r\n        <td style=\"width:50%\">\r\n            <div class=\"client-label\">ODBĚRATEL</div>\r\n            <div class=\"client-name\">{{ClientName}}</div>\r\n            <div class=\"issuer-detail\">{{ClientStreet}}</div>\r\n            <div class=\"issuer-detail\">{{ClientPostalCode}} {{ClientCity}}</div>\r\n            <div class=\"issuer-detail\">IČ: {{ClientRegistrationNumber}}</div>\r\n            <div class=\"issuer-detail\">DIČ: {{ClientTaxNumber}}</div>\r\n        </td>\r\n        <td style=\"width:50%; vertical-align:top; padding-left:30px;\">\r\n            <table style=\"width:100%; border-collapse:collapse;\">\r\n                <tr><td class=\"date-label\">DATUM VYSTAVENÍ</td><td class=\"date-value\">{{IssueDate}}</td></tr>\r\n                <tr><td class=\"date-label\">DATUM SPLATNOSTI</td><td class=\"date-value\">{{DueDate}}</td></tr>\r\n                <tr><td class=\"date-label\">DATUM ZDAN. PLNĚNÍ</td><td class=\"date-value\">{{TaxableSupplyDate}}</td></tr>\r\n                <tr><td class=\"date-label\">FORMA ÚHRADY</td><td class=\"date-value\">{{PaymentMethod}}</td></tr>\r\n            </table>\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n<hr class=\"separator\" />\r\n\r\n<!-- ═══════ INVOICE ITEMS TABLE ═══════ -->\r\n<table class=\"items\">\r\n    <thead>\r\n        <tr>\r\n            <th style=\"width:40%\">POPIS POLOŽKY</th>\r\n            <th style=\"text-align:center; width:8%\">MJ</th>\r\n            <th style=\"text-align:center; width:8%\">DPH</th>\r\n            <th style=\"text-align:center; width:8%\">POČET</th>\r\n            <th style=\"text-align:right; width:18%\">CENA/MJ</th>\r\n            <th style=\"text-align:right; width:18%\">CELKEM BEZ DPH</th>\r\n        </tr>\r\n    </thead>\r\n    <tbody>\r\n        {{InvoiceItems}}\r\n    </tbody>\r\n</table>\r\n\r\n<!-- ═══════ VAT BREAKDOWN + GRAND TOTAL ═══════ -->\r\n<table class=\"vat-summary\">\r\n    <thead>\r\n        <tr>\r\n            <th style=\"width:34%\">SAZBA</th>\r\n            <th style=\"width:33%; text-align:right\">ZÁKLAD</th>\r\n            <th style=\"width:33%; text-align:right\">DPH</th>\r\n        </tr>\r\n    </thead>\r\n    <tbody>\r\n        {{VatBreakdown}}\r\n    </tbody>\r\n</table>\r\n\r\n<table class=\"vat-summary\" style=\"margin-top:0\">\r\n    <tr class=\"grand-total-row\">\r\n        <td style=\"width:67%\">CELKEM K ÚHRADĚ</td>\r\n        <td style=\"width:33%; text-align:right\">{{TotalWithVat}} {{CurrencySymbol}}</td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ QR CODE (if available) ═══════ -->\r\n<table class=\"payment-info\">\r\n    <tr>\r\n        <td style=\"text-align:right\">\r\n            {{QrCodeImage}}\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n</body>\r\n</html>", true, true, "Default Invoice PDF", null, 1, null, null },
                    { 2L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Default HTML template for rendering credit note PDFs.", "<!DOCTYPE html>\r\n<html>\r\n<head>\r\n<meta charset=\"utf-8\" />\r\n<style>\r\n    body {\r\n        font-family: Arial, Helvetica, sans-serif;\r\n        font-size: 11px;\r\n        color: #333;\r\n        margin: 25px 30px;\r\n        line-height: 1.4;\r\n    }\r\n\r\n    /* ── Supplier (DODAVATEL) section ── */\r\n    table.issuer-block {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-bottom: 15px;\r\n    }\r\n    table.issuer-block td {\r\n        vertical-align: top;\r\n        padding: 2px 0;\r\n    }\r\n    .issuer-label {\r\n        font-size: 10px;\r\n        color: #777;\r\n        text-transform: uppercase;\r\n        letter-spacing: 0.5px;\r\n        padding-bottom: 4px;\r\n    }\r\n    .issuer-name {\r\n        font-size: 13px;\r\n        font-weight: bold;\r\n        color: #333;\r\n    }\r\n    .issuer-detail {\r\n        color: #555;\r\n        font-size: 11px;\r\n    }\r\n\r\n    /* ── Blue header bar (document type + number) ── */\r\n    table.doc-header {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin: 12px 0 15px 0;\r\n    }\r\n    .doc-header-bar {\r\n        background-color: #A05050;\r\n        color: #fff;\r\n        padding: 8px 12px;\r\n        font-size: 12px;\r\n    }\r\n    .doc-header-bar .doc-type {\r\n        font-size: 14px;\r\n        font-weight: bold;\r\n        text-transform: uppercase;\r\n    }\r\n    .doc-header-bar .doc-subtitle {\r\n        font-size: 10px;\r\n        font-weight: bold;\r\n        text-transform: uppercase;\r\n    }\r\n    .doc-number-cell {\r\n        background-color: #A05050;\r\n        color: #fff;\r\n        text-align: right;\r\n        padding: 8px 12px;\r\n        font-size: 22px;\r\n        font-weight: bold;\r\n        letter-spacing: 1px;\r\n    }\r\n\r\n    /* ── Client (ODBĚRATEL) + dates section ── */\r\n    table.client-dates {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-bottom: 20px;\r\n    }\r\n    table.client-dates td {\r\n        vertical-align: top;\r\n        padding: 2px 0;\r\n    }\r\n    .client-label {\r\n        font-size: 10px;\r\n        color: #777;\r\n        text-transform: uppercase;\r\n        letter-spacing: 0.5px;\r\n        padding-bottom: 4px;\r\n    }\r\n    .client-name {\r\n        font-weight: bold;\r\n        font-size: 12px;\r\n    }\r\n    .date-label {\r\n        color: #777;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        padding: 3px 10px 3px 0;\r\n    }\r\n    .date-value {\r\n        text-align: right;\r\n        font-weight: bold;\r\n        padding: 3px 0;\r\n    }\r\n\r\n    /* ── Items table ── */\r\n    table.items {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin: 5px 0 15px 0;\r\n    }\r\n    table.items th {\r\n        background-color: #A05050;\r\n        color: #fff;\r\n        padding: 6px 8px;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        font-weight: bold;\r\n        text-align: left;\r\n    }\r\n    table.items td {\r\n        padding: 6px 8px;\r\n        border-bottom: 1px solid #e0e0e0;\r\n        font-size: 11px;\r\n    }\r\n\r\n    /* ── VAT breakdown + grand total ── */\r\n    table.vat-summary {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-top: 10px;\r\n    }\r\n    table.vat-summary th {\r\n        background-color: #e8e8e8;\r\n        padding: 5px 10px;\r\n        font-size: 10px;\r\n        text-transform: uppercase;\r\n        font-weight: bold;\r\n        text-align: center;\r\n    }\r\n    table.vat-summary td {\r\n        padding: 4px 10px;\r\n        font-size: 11px;\r\n    }\r\n    .grand-total-row {\r\n        background-color: #f0f0f0;\r\n    }\r\n    .grand-total-row td {\r\n        font-weight: bold;\r\n        font-size: 13px;\r\n        padding: 8px 10px;\r\n        text-transform: uppercase;\r\n    }\r\n\r\n    /* ── Payment info + QR code ── */\r\n    table.payment-info {\r\n        width: 100%;\r\n        border-collapse: collapse;\r\n        margin-top: 20px;\r\n    }\r\n    table.payment-info td {\r\n        vertical-align: top;\r\n        padding: 2px 5px;\r\n    }\r\n    .payment-label {\r\n        color: #777;\r\n        font-size: 10px;\r\n    }\r\n\r\n    /* ── Separator line ── */\r\n    .separator {\r\n        border: none;\r\n        border-top: 1px solid #ccc;\r\n        margin: 10px 0;\r\n    }\r\n</style>\r\n</head>\r\n<body>\r\n\r\n<!-- ═══════ ISSUER (DODAVATEL) SECTION ═══════ -->\r\n<table class=\"issuer-block\">\r\n    <tr>\r\n        <td style=\"width:50%\">\r\n            <div class=\"issuer-label\">DODAVATEL</div>\r\n            <div class=\"issuer-name\">{{IssuerName}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerStreet}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerPostalCode}} {{IssuerCity}}</div>\r\n            <div class=\"issuer-detail\">{{IssuerCountry}}</div>\r\n        </td>\r\n        <td style=\"width:50%; text-align:right\">\r\n            <table style=\"margin-left:auto; border-collapse:collapse;\">\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">Tel:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerPhone}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">Email:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerEmail}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">IČ:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerRegistrationNumber}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">DIČ:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{IssuerTaxNumber}}</td></tr>\r\n                <tr><td class=\"issuer-detail\" style=\"text-align:left; padding:1px 5px;\">B.Ú.:</td><td class=\"issuer-detail\" style=\"padding:1px 5px;\">{{BankAccountNumber}}</td></tr>\r\n            </table>\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ DOCUMENT HEADER BAR ═══════ -->\r\n<table class=\"doc-header\">\r\n    <tr>\r\n        <td class=\"doc-header-bar\" style=\"width:60%\">\r\n            <span class=\"doc-type\">{{DocumentTypeLabel}}</span>\r\n            <span style=\"font-size:11px; font-weight:normal;\">(variabilní symbol)</span>\r\n            <br/>\r\n            <span class=\"doc-subtitle\">DAŇOVÝ DOKLAD</span>\r\n        </td>\r\n        <td class=\"doc-number-cell\">{{VariableSymbol}}</td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ CLIENT (ODBĚRATEL) + DATES ═══════ -->\r\n<table class=\"client-dates\">\r\n    <tr>\r\n        <td style=\"width:50%\">\r\n            <div class=\"client-label\">ODBĚRATEL</div>\r\n            <div class=\"client-name\">{{ClientName}}</div>\r\n            <div class=\"issuer-detail\">{{ClientStreet}}</div>\r\n            <div class=\"issuer-detail\">{{ClientPostalCode}} {{ClientCity}}</div>\r\n            <div class=\"issuer-detail\">IČ: {{ClientRegistrationNumber}}</div>\r\n            <div class=\"issuer-detail\">DIČ: {{ClientTaxNumber}}</div>\r\n        </td>\r\n        <td style=\"width:50%; vertical-align:top; padding-left:30px;\">\r\n            <table style=\"width:100%; border-collapse:collapse;\">\r\n                <tr><td class=\"date-label\">DATUM VYSTAVENÍ</td><td class=\"date-value\">{{IssueDate}}</td></tr>\r\n                <tr><td class=\"date-label\">DATUM SPLATNOSTI</td><td class=\"date-value\">{{DueDate}}</td></tr>\r\n                <tr><td class=\"date-label\">DATUM ZDAN. PLNĚNÍ</td><td class=\"date-value\">{{TaxableSupplyDate}}</td></tr>\r\n                <tr><td class=\"date-label\">FORMA ÚHRADY</td><td class=\"date-value\">{{PaymentMethod}}</td></tr>\r\n            </table>\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n<hr class=\"separator\" />\r\n\r\n<!-- ═══════ INVOICE ITEMS TABLE ═══════ -->\r\n<table class=\"items\">\r\n    <thead>\r\n        <tr>\r\n            <th style=\"width:40%\">POPIS POLOŽKY</th>\r\n            <th style=\"text-align:center; width:8%\">MJ</th>\r\n            <th style=\"text-align:center; width:8%\">DPH</th>\r\n            <th style=\"text-align:center; width:8%\">POČET</th>\r\n            <th style=\"text-align:right; width:18%\">CENA/MJ</th>\r\n            <th style=\"text-align:right; width:18%\">CELKEM BEZ DPH</th>\r\n        </tr>\r\n    </thead>\r\n    <tbody>\r\n        {{InvoiceItems}}\r\n    </tbody>\r\n</table>\r\n\r\n<!-- ═══════ VAT BREAKDOWN + GRAND TOTAL ═══════ -->\r\n<table class=\"vat-summary\">\r\n    <thead>\r\n        <tr>\r\n            <th style=\"width:34%\">SAZBA</th>\r\n            <th style=\"width:33%; text-align:right\">ZÁKLAD</th>\r\n            <th style=\"width:33%; text-align:right\">DPH</th>\r\n        </tr>\r\n    </thead>\r\n    <tbody>\r\n        {{VatBreakdown}}\r\n    </tbody>\r\n</table>\r\n\r\n<table class=\"vat-summary\" style=\"margin-top:0\">\r\n    <tr class=\"grand-total-row\">\r\n        <td style=\"width:67%\">CELKEM K ÚHRADĚ</td>\r\n        <td style=\"width:33%; text-align:right\">{{TotalWithVat}} {{CurrencySymbol}}</td>\r\n    </tr>\r\n</table>\r\n\r\n<!-- ═══════ QR CODE (if available) ═══════ -->\r\n<table class=\"payment-info\">\r\n    <tr>\r\n        <td style=\"text-align:right\">\r\n            {{QrCodeImage}}\r\n        </td>\r\n    </tr>\r\n</table>\r\n\r\n</body>\r\n</html>", true, true, "Default Credit Note PDF", null, 2, null, null },
                    { 3L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email body when sending an invoice.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Invoice {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached invoice <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><br/><p>Thank you for your business.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{CompanyName}}</p></div>", true, true, "Default Invoice Email", "Invoice {{InvoiceNumber}} from {{CompanyName}}", 10, null, null },
                    { 4L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email body when sending a credit note.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Credit Note {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached credit note <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Thank you for your business.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{CompanyName}}</p></div>", true, true, "Default Credit Note Email", "Credit Note {{InvoiceNumber}} from {{CompanyName}}", 11, null, null },
                    { 5L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Email sent to new users.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #1976D2;\">Welcome to {{AppName}}</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>You have been invited to {{AppName}}. Please set your password by clicking the button below:</p><div style=\"text-align: center; margin: 30px 0;\"><a href=\"{{InvitationLink}}\" style=\"background-color: #1976D2; color: white; padding: 14px 28px; text-decoration: none; border-radius: 4px; font-size: 16px;\">Set Password</a></div><p style=\"color: #666; font-size: 14px;\">This link is valid for 48 hours.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{AppName}}</p></div>", true, true, "Default Invitation Email", "Invitation to {{AppName}} — Set your password", 20, null, null },
                    { 6L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Payment reminder for overdue invoices.", "<div style=\"font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;\"><h2 style=\"color: #E65100;\">Payment Reminder</h2><p>Dear customer,</p><p>This is a friendly reminder that invoice <strong>{{InvoiceNumber}}</strong> is overdue.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><p>Please arrange payment at your earliest convenience.</p><br/><p>Thank you.</p><hr style=\"border: none; border-top: 1px solid #eee; margin: 20px 0;\" /><p style=\"color: #999; font-size: 12px;\">{{CompanyName}}</p></div>", true, true, "Default Payment Reminder", "Payment reminder — Invoice {{InvoiceNumber}}", 21, null, null }
                });

            migrationBuilder.InsertData(
                schema: "tenant_template",
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
                schema: "tenant_template",
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
                schema: "tenant_template",
                table: "VatRate",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "IsActive", "IsDefault", "IsReduced", "Name", "Rate", "UpdatedAt", "UpdatedByUserId", "ValidFrom", "ValidTo" },
                values: new object[,]
                {
                    { 1L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, true, false, "DPH 21% - standardní sazba", 21.00m, null, null, new DateTime(2013, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 2L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, true, true, "DPH 12% - snížená sazba", 12.00m, null, null, new DateTime(2015, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 3L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, false, false, "DPH 0% - osvobozeno od daně", 0.00m, null, null, new DateTime(2013, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });

            migrationBuilder.InsertData(
                schema: "tenant_template",
                table: "NumberSequence",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "CurrentMonth", "CurrentNumber", "CurrentYear", "DocumentType", "IsActive", "IsDefault", "Name", "NumberSequenceFormatId", "Prefix", "Suffix", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { 1L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, 0, null, 1, true, true, "Default Invoice Sequence", 1L, "INV", null, null, null },
                    { 2L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, 0, null, 2, true, true, "Default Credit Note Sequence", 1L, "CN", null, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Address_AddressType",
                schema: "tenant_template",
                table: "Address",
                column: "AddressType");

            migrationBuilder.CreateIndex(
                name: "IX_Address_ClientId",
                schema: "tenant_template",
                table: "Address",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_AresCache_ExpiresAt",
                schema: "tenant_template",
                table: "AresCache",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AresCache_RegistrationNumber",
                schema: "tenant_template",
                table: "AresCache",
                column: "RegistrationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankAccount_ClientId",
                schema: "tenant_template",
                table: "BankAccount",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_BankAccount_ClientId_IsDefault",
                schema: "tenant_template",
                table: "BankAccount",
                columns: new[] { "ClientId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingSettings_ClientId",
                schema: "tenant_template",
                table: "BillingSettings",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingSettings_CustomCreditNoteNumberSequenceId",
                schema: "tenant_template",
                table: "BillingSettings",
                column: "CustomCreditNoteNumberSequenceId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingSettings_CustomInvoiceNumberSequenceId",
                schema: "tenant_template",
                table: "BillingSettings",
                column: "CustomInvoiceNumberSequenceId");

            migrationBuilder.CreateIndex(
                name: "IX_Client_IsActive",
                schema: "tenant_template",
                table: "Client",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Client_IsIssuer",
                schema: "tenant_template",
                table: "Client",
                column: "IsIssuer");

            migrationBuilder.CreateIndex(
                name: "IX_Client_PreferredCurrencyId",
                schema: "tenant_template",
                table: "Client",
                column: "PreferredCurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Client_RegistrationNumber",
                schema: "tenant_template",
                table: "Client",
                column: "RegistrationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contact_ClientId",
                schema: "tenant_template",
                table: "Contact",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Contact_ContactType",
                schema: "tenant_template",
                table: "Contact",
                column: "ContactType");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_IsActive",
                schema: "tenant_template",
                table: "ContentTemplate",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_IsDefault",
                schema: "tenant_template",
                table: "ContentTemplate",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_TemplateType",
                schema: "tenant_template",
                table: "ContentTemplate",
                column: "TemplateType");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_TemplateType_IsDefault",
                schema: "tenant_template",
                table: "ContentTemplate",
                columns: new[] { "TemplateType", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_Currency_Code",
                schema: "tenant_template",
                table: "Currency",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Currency_IsActive",
                schema: "tenant_template",
                table: "Currency",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Currency_SortOrder",
                schema: "tenant_template",
                table: "Currency",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_Category",
                schema: "tenant_template",
                table: "Invoice",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_ClientId",
                schema: "tenant_template",
                table: "Invoice",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_CurrencyId",
                schema: "tenant_template",
                table: "Invoice",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_DocumentNumber",
                schema: "tenant_template",
                table: "Invoice",
                column: "DocumentNumber",
                unique: true,
                filter: "\"DocumentNumber\" IS NOT NULL AND \"DocumentNumber\" <> 'DRAFT'");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_DocumentType",
                schema: "tenant_template",
                table: "Invoice",
                column: "DocumentType");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_DueDate",
                schema: "tenant_template",
                table: "Invoice",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_IsActive",
                schema: "tenant_template",
                table: "Invoice",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_IssueDate",
                schema: "tenant_template",
                table: "Invoice",
                column: "IssueDate");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_IssuerId",
                schema: "tenant_template",
                table: "Invoice",
                column: "IssuerId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_Name",
                schema: "tenant_template",
                table: "Invoice",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_NumberSequenceId",
                schema: "tenant_template",
                table: "Invoice",
                column: "NumberSequenceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_OriginalInvoiceId",
                schema: "tenant_template",
                table: "Invoice",
                column: "OriginalInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_Status",
                schema: "tenant_template",
                table: "Invoice",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItem_InvoiceId",
                schema: "tenant_template",
                table: "InvoiceItem",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItem_VatRateId",
                schema: "tenant_template",
                table: "InvoiceItem",
                column: "VatRateId");

            migrationBuilder.CreateIndex(
                name: "IX_NumberSequence_DocumentType_IsDefault",
                schema: "tenant_template",
                table: "NumberSequence",
                columns: new[] { "DocumentType", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_NumberSequence_IsActive",
                schema: "tenant_template",
                table: "NumberSequence",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_NumberSequence_NumberSequenceFormatId",
                schema: "tenant_template",
                table: "NumberSequence",
                column: "NumberSequenceFormatId");

            migrationBuilder.CreateIndex(
                name: "IX_NumberSequenceFormat_IsActive",
                schema: "tenant_template",
                table: "NumberSequenceFormat",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_IsActive",
                schema: "tenant_template",
                table: "VatRate",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_IsDefault",
                schema: "tenant_template",
                table: "VatRate",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_IsDefault_IsReduced",
                schema: "tenant_template",
                table: "VatRate",
                columns: new[] { "IsDefault", "IsReduced" });

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_ValidFrom",
                schema: "tenant_template",
                table: "VatRate",
                column: "ValidFrom");

            migrationBuilder.CreateIndex(
                name: "IX_VatRate_ValidTo",
                schema: "tenant_template",
                table: "VatRate",
                column: "ValidTo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Address",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "AresCache",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "BankAccount",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "BillingSettings",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "Contact",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "ContentTemplate",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "InvoiceItem",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "Invoice",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "VatRate",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "Client",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "NumberSequence",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "Currency",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "NumberSequenceFormat",
                schema: "tenant_template");
        }
    }
}
