using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Reminder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<long>(type: "bigint", nullable: false),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReminderDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InvoiceAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FeeCzk = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    InterestCzk = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalCzk = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentToEmail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reminder", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reminder_Client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reminder_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReminderSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<long>(type: "bigint", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    MaxReminderLevel = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    GracePeriodDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 7),
                    IncludeInterest = table.Column<bool>(type: "boolean", nullable: false),
                    AttachInvoicePdf = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AutoSendEmail = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReminderSettings_Client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReminderLevel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReminderSettingsId = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    DaysAfterPrevious = table.Column<int>(type: "integer", nullable: false, defaultValue: 7),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FixedFeeCzk = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    EmailTemplateId = table.Column<long>(type: "bigint", nullable: true),
                    PdfTemplateId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderLevel", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReminderLevel_ContentTemplate_EmailTemplateId",
                        column: x => x.EmailTemplateId,
                        principalTable: "ContentTemplate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ReminderLevel_ContentTemplate_PdfTemplateId",
                        column: x => x.PdfTemplateId,
                        principalTable: "ContentTemplate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ReminderLevel_ReminderSettings_ReminderSettingsId",
                        column: x => x.ReminderSettingsId,
                        principalTable: "ReminderSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reminder_ClientId",
                table: "Reminder",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Reminder_InvoiceId_Level",
                table: "Reminder",
                columns: new[] { "InvoiceId", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reminder_ReminderDate",
                table: "Reminder",
                column: "ReminderDate");

            migrationBuilder.CreateIndex(
                name: "IX_Reminder_Status",
                table: "Reminder",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderLevel_EmailTemplateId",
                table: "ReminderLevel",
                column: "EmailTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderLevel_PdfTemplateId",
                table: "ReminderLevel",
                column: "PdfTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderLevel_ReminderSettingsId_Level",
                table: "ReminderLevel",
                columns: new[] { "ReminderSettingsId", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReminderSettings_ClientId",
                table: "ReminderSettings",
                column: "ClientId",
                unique: true,
                filter: "\"ClientId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reminder");

            migrationBuilder.DropTable(
                name: "ReminderLevel");

            migrationBuilder.DropTable(
                name: "ReminderSettings");
        }
    }
}
