using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddOssVatRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OssRegistered",
                table: "CompanySystemSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "OssRegisteredSince",
                table: "CompanySystemSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OssVatRate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OssVatRate", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OssVatRate_CountryCode",
                table: "OssVatRate",
                column: "CountryCode");

            migrationBuilder.CreateIndex(
                name: "IX_OssVatRate_CountryCode_IsActive",
                table: "OssVatRate",
                columns: new[] { "CountryCode", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_OssVatRate_CountryCode_Rate_ValidFrom",
                table: "OssVatRate",
                columns: new[] { "CountryCode", "Rate", "ValidFrom" },
                unique: true);

            // Seed standard + reduced rates of the 26 EU member states other than CZ.
            // Source: European Commission, "VAT rates applied in the Member States of the European Union"
            // (taxation-customs.ec.europa.eu/taxation/vat/eu-vat-rules-topic/vat-rates_en / TEDB), as known on 2026-10-02.
            // ponytail: hand-transcribed, only the standard and the main reduced rate per state (some states have
            // more reduced/super-reduced/parking rates). SysAdmin must verify against TEDB before the first real
            // OSS filing and add missing rows via POST /api/oss-vat-rate (ADMINGUIDE).
            // All rows start at 2021-07-01 (OSS start): rate changes after that date (e.g. EE 24 % from 2025-07) are NOT historized.
            // Idempotent: unique index (CountryCode, Rate, ValidFrom) + ON CONFLICT DO NOTHING; Id comes from the
            // identity column (never hardcoded, so later inserts through the API cannot collide). Category: 0 = Standard, 1 = Reduced.
            migrationBuilder.Sql(@"
INSERT INTO ""OssVatRate"" (""CountryCode"", ""Rate"", ""Category"", ""Description"", ""ValidFrom"", ""IsActive"", ""CreatedAt"")
SELECT v.cc, v.rate, v.cat, v.descr, DATE '2021-07-01', TRUE, NOW()
FROM (VALUES
                ('AT', 20.00, 0, 'Standardní sazba'),
                ('AT', 10.00, 1, 'Snížená sazba'),
                ('BE', 21.00, 0, 'Standardní sazba'),
                ('BE', 6.00, 1, 'Snížená sazba'),
                ('BG', 20.00, 0, 'Standardní sazba'),
                ('BG', 9.00, 1, 'Snížená sazba'),
                ('HR', 25.00, 0, 'Standardní sazba'),
                ('HR', 13.00, 1, 'Snížená sazba'),
                ('CY', 19.00, 0, 'Standardní sazba'),
                ('CY', 9.00, 1, 'Snížená sazba'),
                ('DK', 25.00, 0, 'Standardní sazba'),
                ('EE', 24.00, 0, 'Standardní sazba (od 7/2025)'),
                ('EE', 9.00, 1, 'Snížená sazba'),
                ('FI', 25.50, 0, 'Standardní sazba (od 2024)'),
                ('FI', 14.00, 1, 'Snížená sazba'),
                ('FR', 20.00, 0, 'Standardní sazba'),
                ('FR', 10.00, 1, 'Snížená sazba'),
                ('DE', 19.00, 0, 'Standardní sazba'),
                ('DE', 7.00, 1, 'Snížená sazba'),
                ('GR', 24.00, 0, 'Standardní sazba'),
                ('GR', 13.00, 1, 'Snížená sazba'),
                ('HU', 27.00, 0, 'Standardní sazba'),
                ('HU', 18.00, 1, 'Snížená sazba'),
                ('IE', 23.00, 0, 'Standardní sazba'),
                ('IE', 13.50, 1, 'Snížená sazba'),
                ('IT', 22.00, 0, 'Standardní sazba'),
                ('IT', 10.00, 1, 'Snížená sazba'),
                ('LV', 21.00, 0, 'Standardní sazba'),
                ('LV', 12.00, 1, 'Snížená sazba'),
                ('LT', 21.00, 0, 'Standardní sazba'),
                ('LT', 9.00, 1, 'Snížená sazba'),
                ('LU', 17.00, 0, 'Standardní sazba'),
                ('LU', 8.00, 1, 'Snížená sazba'),
                ('MT', 18.00, 0, 'Standardní sazba'),
                ('MT', 7.00, 1, 'Snížená sazba'),
                ('NL', 21.00, 0, 'Standardní sazba'),
                ('NL', 9.00, 1, 'Snížená sazba'),
                ('PL', 23.00, 0, 'Standardní sazba'),
                ('PL', 8.00, 1, 'Snížená sazba'),
                ('PT', 23.00, 0, 'Standardní sazba'),
                ('PT', 13.00, 1, 'Snížená sazba'),
                ('RO', 21.00, 0, 'Standardní sazba (od 8/2025)'),
                ('RO', 11.00, 1, 'Snížená sazba'),
                ('SK', 23.00, 0, 'Standardní sazba (od 2025)'),
                ('SK', 19.00, 1, 'Snížená sazba'),
                ('SI', 22.00, 0, 'Standardní sazba'),
                ('SI', 9.50, 1, 'Snížená sazba'),
                ('ES', 21.00, 0, 'Standardní sazba'),
                ('ES', 10.00, 1, 'Snížená sazba'),
                ('SE', 25.00, 0, 'Standardní sazba'),
                ('SE', 12.00, 1, 'Snížená sazba')
) AS v(cc, rate, cat, descr)
ON CONFLICT (""CountryCode"", ""Rate"", ""ValidFrom"") DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OssVatRate");

            migrationBuilder.DropColumn(
                name: "OssRegistered",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "OssRegisteredSince",
                table: "CompanySystemSettings");
        }
    }
}
