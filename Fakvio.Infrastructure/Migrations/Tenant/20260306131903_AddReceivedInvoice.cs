using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddReceivedInvoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReceivedInvoice",
                schema: "tenant_template",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    IssueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TaxableSupplyDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VariableSymbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TotalBeforeVat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalVat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalWithVat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    PaymentMethod = table.Column<int>(type: "integer", nullable: true),
                    BankAccountNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IBAN = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SWIFT = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    AttachmentFileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AttachmentContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivedInvoice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceivedInvoice_Client_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "tenant_template",
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceivedInvoice_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalSchema: "tenant_template",
                        principalTable: "Currency",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceivedInvoiceItem",
                schema: "tenant_template",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReceivedInvoiceId = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_ReceivedInvoiceItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceivedInvoiceItem_ReceivedInvoice_ReceivedInvoiceId",
                        column: x => x.ReceivedInvoiceId,
                        principalSchema: "tenant_template",
                        principalTable: "ReceivedInvoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReceivedInvoiceItem_VatRate_VatRateId",
                        column: x => x.VatRateId,
                        principalSchema: "tenant_template",
                        principalTable: "VatRate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoice_CurrencyId",
                schema: "tenant_template",
                table: "ReceivedInvoice",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoice_DueDate",
                schema: "tenant_template",
                table: "ReceivedInvoice",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoice_IssueDate",
                schema: "tenant_template",
                table: "ReceivedInvoice",
                column: "IssueDate");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoice_ReceivedDate",
                schema: "tenant_template",
                table: "ReceivedInvoice",
                column: "ReceivedDate");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoice_Status",
                schema: "tenant_template",
                table: "ReceivedInvoice",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoice_SupplierId",
                schema: "tenant_template",
                table: "ReceivedInvoice",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoice_TaxableSupplyDate",
                schema: "tenant_template",
                table: "ReceivedInvoice",
                column: "TaxableSupplyDate");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoiceItem_ReceivedInvoiceId",
                schema: "tenant_template",
                table: "ReceivedInvoiceItem",
                column: "ReceivedInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedInvoiceItem_VatRateId",
                schema: "tenant_template",
                table: "ReceivedInvoiceItem",
                column: "VatRateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceivedInvoiceItem",
                schema: "tenant_template");

            migrationBuilder.DropTable(
                name: "ReceivedInvoice",
                schema: "tenant_template");
        }
    }
}
