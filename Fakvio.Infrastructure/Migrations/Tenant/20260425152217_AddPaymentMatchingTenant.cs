using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddPaymentMatchingTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PaidAmount",
                table: "Invoice",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "BankAccountMailbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankAccountId = table.Column<long>(type: "bigint", nullable: false),
                    InboundAlias = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastEmailReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmailsReceivedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccountMailbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankAccountMailbox_BankAccount_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "BankAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankTransaction",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankAccountId = table.Column<long>(type: "bigint", nullable: false),
                    DeduplicationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    VariableSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ConstantSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SpecificSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CounterpartyAccount = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CounterpartyName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ImportSource = table.Column<int>(type: "integer", nullable: false),
                    RawPayload = table.Column<string>(type: "text", nullable: true),
                    ParserConfidence = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    ParserModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MatchStatus = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankTransaction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankTransaction_BankAccount_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "BankAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InboundEmail",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankAccountMailboxId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ImapUid = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ServerReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FromAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FromDisplayName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ToAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EmailDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TextBody = table.Column<string>(type: "text", nullable: true),
                    HtmlBody = table.Column<string>(type: "text", nullable: true),
                    BodyTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    DeduplicationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParseStatus = table.Column<int>(type: "integer", nullable: false),
                    ParseError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ParseAttempts = table.Column<int>(type: "integer", nullable: false),
                    BankTransactionId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundEmail", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboundEmail_BankAccountMailbox_BankAccountMailboxId",
                        column: x => x.BankAccountMailboxId,
                        principalTable: "BankAccountMailbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboundEmail_BankTransaction_BankTransactionId",
                        column: x => x.BankTransactionId,
                        principalTable: "BankTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMatch",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankTransactionId = table.Column<long>(type: "bigint", nullable: false),
                    InvoiceId = table.Column<long>(type: "bigint", nullable: true),
                    ReceivedInvoiceId = table.Column<long>(type: "bigint", nullable: true),
                    MatchedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MatchedBy = table.Column<int>(type: "integer", nullable: false),
                    MatchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MatchedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMatch", x => x.Id);
                    table.CheckConstraint("CK_PaymentMatch_Target", "\"InvoiceId\" IS NOT NULL OR \"ReceivedInvoiceId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_PaymentMatch_BankTransaction_BankTransactionId",
                        column: x => x.BankTransactionId,
                        principalTable: "BankTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentMatch_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentMatch_ReceivedInvoice_ReceivedInvoiceId",
                        column: x => x.ReceivedInvoiceId,
                        principalTable: "ReceivedInvoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccountMailbox_BankAccountId",
                table: "BankAccountMailbox",
                column: "BankAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankAccountMailbox_InboundAlias",
                table: "BankAccountMailbox",
                column: "InboundAlias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankAccountMailbox_IsActive",
                table: "BankAccountMailbox",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransaction_BankAccountId_DeduplicationHash",
                table: "BankTransaction",
                columns: new[] { "BankAccountId", "DeduplicationHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankTransaction_BankAccountId_TransactionDate",
                table: "BankTransaction",
                columns: new[] { "BankAccountId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransaction_MatchStatus",
                table: "BankTransaction",
                column: "MatchStatus");

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmail_BankAccountMailboxId_DeduplicationHash",
                table: "InboundEmail",
                columns: new[] { "BankAccountMailboxId", "DeduplicationHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmail_BankTransactionId",
                table: "InboundEmail",
                column: "BankTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmail_ParseStatus",
                table: "InboundEmail",
                column: "ParseStatus");

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmail_ServerReceivedAt",
                table: "InboundEmail",
                column: "ServerReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMatch_BankTransactionId",
                table: "PaymentMatch",
                column: "BankTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMatch_InvoiceId",
                table: "PaymentMatch",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMatch_ReceivedInvoiceId",
                table: "PaymentMatch",
                column: "ReceivedInvoiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboundEmail");

            migrationBuilder.DropTable(
                name: "PaymentMatch");

            migrationBuilder.DropTable(
                name: "BankAccountMailbox");

            migrationBuilder.DropTable(
                name: "BankTransaction");

            migrationBuilder.DropColumn(
                name: "PaidAmount",
                table: "Invoice");
        }
    }
}
