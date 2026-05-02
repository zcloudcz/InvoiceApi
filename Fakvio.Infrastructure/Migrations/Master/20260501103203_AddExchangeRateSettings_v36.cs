using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddExchangeRateSettings_v36 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExchangeRateLastRunAt",
                table: "CompanySystemSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExchangeRateUpdateDayOfWeek",
                table: "CompanySystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExchangeRateUpdateMode",
                table: "CompanySystemSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExchangeRateLastRunAt",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "ExchangeRateUpdateDayOfWeek",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "ExchangeRateUpdateMode",
                table: "CompanySystemSettings");
        }
    }
}
