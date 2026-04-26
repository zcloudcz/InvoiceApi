using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddPaymentMatchingMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MasterMailboxIndex",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InboundAlias = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TenantSchema = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantBankAccountMailboxId = table.Column<long>(type: "bigint", nullable: false),
                    IsAliasRetired = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterMailboxIndex", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMatchingSystemSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ImapHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ImapPort = table.Column<int>(type: "integer", nullable: false),
                    ImapUseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    ImapUsername = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ImapPasswordEncrypted = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ImapFolder = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "INBOX"),
                    ProcessedFolder = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Processed"),
                    UnroutedFolder = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Unrouted"),
                    InboundDomain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: "pay.fakvio.cz"),
                    PollIntervalMinutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    InboundEmailRetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 1825),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunStatus = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastRunProcessedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMatchingSystemSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MasterMailboxIndex_InboundAlias",
                table: "MasterMailboxIndex",
                column: "InboundAlias",
                unique: true,
                filter: "\"IsAliasRetired\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_MasterMailboxIndex_TenantSchema",
                table: "MasterMailboxIndex",
                column: "TenantSchema");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MasterMailboxIndex");

            migrationBuilder.DropTable(
                name: "PaymentMatchingSystemSettings");
        }
    }
}
