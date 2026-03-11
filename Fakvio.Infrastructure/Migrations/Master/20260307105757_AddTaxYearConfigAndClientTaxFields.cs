using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddTaxYearConfigAndClientTaxFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActivityType",
                table: "Client",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FlatRateBand",
                table: "Client",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMainActivity",
                table: "Client",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TaxRegime",
                table: "Client",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaxYearConfig",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Country = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    AverageMonthlyWage = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LivingMinimum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IncomeTaxRate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    ProgressiveTaxRate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    ProgressiveThreshold = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BasicTaxpayerCredit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SocialInsuranceRate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    SocialAssessmentBasePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    MinMonthlySocialMain = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MinMonthlySocialSecondary = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxSocialAssessmentBase = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    HealthInsuranceRate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    HealthAssessmentBasePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    MinMonthlyHealthMain = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxHealthAssessmentBase = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FlatRateBand1Monthly = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FlatRateBand2Monthly = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FlatRateBand3Monthly = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LumpSum80Cap = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LumpSum60Cap = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LumpSum40Cap = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LumpSum30Cap = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxYearConfig", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "TaxYearConfig",
                columns: new[] { "Id", "AverageMonthlyWage", "BasicTaxpayerCredit", "Country", "CreatedAt", "CreatedByUserId", "CurrencyCode", "FlatRateBand1Monthly", "FlatRateBand2Monthly", "FlatRateBand3Monthly", "HealthAssessmentBasePercent", "HealthInsuranceRate", "IncomeTaxRate", "LivingMinimum", "LumpSum30Cap", "LumpSum40Cap", "LumpSum60Cap", "LumpSum80Cap", "MaxHealthAssessmentBase", "MaxSocialAssessmentBase", "MinMonthlyHealthMain", "MinMonthlySocialMain", "MinMonthlySocialSecondary", "ProgressiveTaxRate", "ProgressiveThreshold", "SocialAssessmentBasePercent", "SocialInsuranceRate", "UpdatedAt", "UpdatedByUserId", "Year" },
                values: new object[,]
                {
                    { 1L, 43967m, 30840m, "CZ", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), null, "CZK", 7498m, 16000m, 26000m, 50m, 13.5m, 15m, 4860m, 600000m, 800000m, 1200000m, 1600000m, 0m, 25324992m, 2968m, 3852m, 0m, 23m, 1582812m, 50m, 29.2m, null, null, 2025 },
                    { 2L, 45617m, 30840m, "CZ", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), null, "CZK", 8716m, 16000m, 26000m, 50m, 13.5m, 15m, 4860m, 600000m, 800000m, 1200000m, 1600000m, 0m, 26275392m, 3079m, 4096m, 0m, 23m, 1642212m, 50m, 29.2m, null, null, 2026 },
                    { 3L, 1430m, 5646.48m, "SK", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), null, "EUR", 0m, 0m, 0m, 50m, 14m, 15m, 268.88m, 0m, 0m, 20000m, 0m, 0m, 120120m, 97.80m, 216.13m, 0m, 25m, 47537.984m, 50m, 33.15m, null, null, 2025 },
                    { 4L, 1500m, 5753.79m, "SK", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), null, "EUR", 0m, 0m, 0m, 50m, 14m, 15m, 273.99m, 0m, 0m, 20000m, 0m, 0m, 126000m, 105m, 225m, 0m, 25m, 48441.432m, 50m, 33.15m, null, null, 2026 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxYearConfig_Year_Country",
                table: "TaxYearConfig",
                columns: new[] { "Year", "Country" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxYearConfig");

            migrationBuilder.DropColumn(
                name: "ActivityType",
                table: "Client");

            migrationBuilder.DropColumn(
                name: "FlatRateBand",
                table: "Client");

            migrationBuilder.DropColumn(
                name: "IsMainActivity",
                table: "Client");

            migrationBuilder.DropColumn(
                name: "TaxRegime",
                table: "Client");
        }
    }
}
