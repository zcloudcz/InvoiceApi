using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddInvoiceEmail_v47 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoiceMailbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    table.PrimaryKey("PK_InvoiceMailbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboundInvoiceEmail",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceMailboxId = table.Column<long>(type: "bigint", nullable: false),
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
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: true),
                    ClassificationConfidence = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    StatusError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProcessAttempts = table.Column<int>(type: "integer", nullable: false),
                    ReceivedInvoiceId = table.Column<long>(type: "bigint", nullable: true),
                    InvoiceId = table.Column<long>(type: "bigint", nullable: true),
                    AttachmentCount = table.Column<int>(type: "integer", nullable: false),
                    HasPdf = table.Column<bool>(type: "boolean", nullable: false),
                    HasIsdoc = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundInvoiceEmail", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboundInvoiceEmail_InvoiceMailbox_InvoiceMailboxId",
                        column: x => x.InvoiceMailboxId,
                        principalTable: "InvoiceMailbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboundInvoiceEmail_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InboundInvoiceEmail_ReceivedInvoice_ReceivedInvoiceId",
                        column: x => x.ReceivedInvoiceId,
                        principalTable: "ReceivedInvoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboundInvoiceEmail_InvoiceId",
                table: "InboundInvoiceEmail",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundInvoiceEmail_InvoiceMailboxId_DeduplicationHash",
                table: "InboundInvoiceEmail",
                columns: new[] { "InvoiceMailboxId", "DeduplicationHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundInvoiceEmail_ReceivedInvoiceId",
                table: "InboundInvoiceEmail",
                column: "ReceivedInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundInvoiceEmail_ServerReceivedAt",
                table: "InboundInvoiceEmail",
                column: "ServerReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InboundInvoiceEmail_Status",
                table: "InboundInvoiceEmail",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceMailbox_InboundAlias",
                table: "InvoiceMailbox",
                column: "InboundAlias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceMailbox_IsActive",
                table: "InvoiceMailbox",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboundInvoiceEmail");

            migrationBuilder.DropTable(
                name: "InvoiceMailbox");
        }
    }
}
