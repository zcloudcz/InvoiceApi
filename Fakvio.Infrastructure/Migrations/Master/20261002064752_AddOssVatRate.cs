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

            // Seed standard + the main reduced rate(s) of the 26 EU member states other than CZ.
            // SOURCES (checked 2026-10-02; the official EC TEDB (ec.europa.eu/taxation_customs/tedb) and the EC
            // "VAT rates applied in the Member States" PDF could not be fetched by tooling, so the table is cross-checked
            // against secondary sources that mirror them as of 1 Jan 2026):
            //   https://www.vatupdate.com/vat-rates-eu/ , https://taxfoundation.org/data/all/eu/value-added-tax-vat-rates-europe/ ,
            //   https://www.eurofiscalis.com/en/vat-rates-in-ue/ , https://hellotax.com/blog/vat-rates-in-europe/
            // HISTORY: only rate changes after the OSS start (2021-07-01) that we are confident about are historized with real
            // ValidFrom/ValidTo: EE (std 20/22/24, reduced 13 from 2025-01-01), FI (std 25.5 from 2024-09-01, reduced 14 -> 13.5
            // on 2026-01-01), RO (std 21 and reduced 11 from 2025-08-01), SK (std 23, reduced 19 from 2025-01-01), LT (reduced 12 from
            // 2026-01-01). Every other row is valid from 2021-07-01 and is NOT checked for earlier changes (e.g. temporary cuts).
            // Only the standard and the main reduced rate(s) are seeded (not super-reduced/parking/accommodation rates).
            // SysAdmin must verify against TEDB before the first real OSS filing and add/adjust rows via /api/oss-vat-rate (ADMINGUIDE).
            // Idempotent: unique index (CountryCode, Rate, ValidFrom) + ON CONFLICT DO NOTHING; Id comes from the identity
            // column (never hardcoded). Category: 0 = Standard, 1 = Reduced.
            migrationBuilder.Sql(@"
INSERT INTO ""OssVatRate"" (""CountryCode"", ""Rate"", ""Category"", ""Description"", ""ValidFrom"", ""ValidTo"", ""IsActive"", ""CreatedAt"")
SELECT v.cc, v.rate, v.cat, v.descr, v.vfrom, v.vto, TRUE, NOW()
FROM (VALUES
                ('AT', 10.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('AT', 20.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('BE', 6.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('BE', 21.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('BG', 9.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('BG', 20.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('CY', 9.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('CY', 19.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('DE', 7.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('DE', 19.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('DK', 25.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('EE', 9.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('EE', 20.0, 0, 'Standardní sazba', DATE '2021-07-01', DATE '2023-12-31'),
                ('EE', 22.0, 0, 'Standardní sazba', DATE '2024-01-01', DATE '2025-06-30'),
                ('EE', 13.0, 1, 'Snížená sazba', DATE '2025-01-01', NULL::date),
                ('EE', 24.0, 0, 'Standardní sazba', DATE '2025-07-01', NULL::date),
                ('ES', 10.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('ES', 21.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('FI', 14.0, 1, 'Snížená sazba', DATE '2021-07-01', DATE '2025-12-31'),
                ('FI', 24.0, 0, 'Standardní sazba', DATE '2021-07-01', DATE '2024-08-31'),
                ('FI', 25.5, 0, 'Standardní sazba', DATE '2024-09-01', NULL::date),
                ('FI', 13.5, 1, 'Snížená sazba', DATE '2026-01-01', NULL::date),
                ('FR', 10.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('FR', 20.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('GR', 13.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('GR', 24.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('HR', 13.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('HR', 25.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('HU', 18.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('HU', 27.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('IE', 13.5, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('IE', 23.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('IT', 10.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('IT', 22.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('LT', 9.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('LT', 21.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('LT', 12.0, 1, 'Snížená sazba', DATE '2026-01-01', NULL::date),
                ('LU', 8.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('LU', 17.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('LV', 12.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('LV', 21.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('MT', 7.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('MT', 18.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('NL', 9.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('NL', 21.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('PL', 8.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('PL', 23.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('PT', 13.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('PT', 23.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('RO', 5.0, 1, 'Snížená sazba', DATE '2021-07-01', DATE '2025-07-31'),
                ('RO', 9.0, 1, 'Snížená sazba', DATE '2021-07-01', DATE '2025-07-31'),
                ('RO', 19.0, 0, 'Standardní sazba', DATE '2021-07-01', DATE '2025-07-31'),
                ('RO', 11.0, 1, 'Snížená sazba', DATE '2025-08-01', NULL::date),
                ('RO', 21.0, 0, 'Standardní sazba', DATE '2025-08-01', NULL::date),
                ('SE', 12.0, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('SE', 25.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('SI', 9.5, 1, 'Snížená sazba', DATE '2021-07-01', NULL::date),
                ('SI', 22.0, 0, 'Standardní sazba', DATE '2021-07-01', NULL::date),
                ('SK', 10.0, 1, 'Snížená sazba', DATE '2021-07-01', DATE '2024-12-31'),
                ('SK', 20.0, 0, 'Standardní sazba', DATE '2021-07-01', DATE '2024-12-31'),
                ('SK', 19.0, 1, 'Snížená sazba', DATE '2025-01-01', NULL::date),
                ('SK', 23.0, 0, 'Standardní sazba', DATE '2025-01-01', NULL::date)
) AS v(cc, rate, cat, descr, vfrom, vto)
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
