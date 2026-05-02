using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddCurrencyRates_v36 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExchangeRate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,5)", precision: 18, scale: 5, nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "CNB"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRate", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRate_CurrencyCode_ValidFrom",
                table: "ExchangeRate",
                columns: new[] { "CurrencyCode", "ValidFrom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeRate");
        }
    }
}
