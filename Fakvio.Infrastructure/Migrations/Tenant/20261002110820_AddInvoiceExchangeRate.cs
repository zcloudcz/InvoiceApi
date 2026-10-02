using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddInvoiceExchangeRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "ReceivedInvoice",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExchangeRateDate",
                table: "ReceivedInvoice",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "Invoice",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExchangeRateDate",
                table: "Invoice",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "ReceivedInvoice");

            migrationBuilder.DropColumn(
                name: "ExchangeRateDate",
                table: "ReceivedInvoice");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ExchangeRateDate",
                table: "Invoice");
        }
    }
}
