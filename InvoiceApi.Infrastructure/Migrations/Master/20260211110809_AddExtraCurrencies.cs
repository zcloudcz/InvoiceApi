using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace InvoiceApi.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddExtraCurrencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Currency",
                columns: new[] { "Id", "Code", "CreatedAt", "CreatedByUserId", "DecimalPlaces", "DisplayFormat", "IsActive", "Name", "SortOrder", "Symbol", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { 6L, "CHF", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "CHF {0:N2}", true, "Swiss Franc", 6, "CHF", null, null },
                    { 7L, "HUF", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 0, "{0:N0} Ft", true, "Hungarian Forint", 7, "Ft", null, null },
                    { 8L, "RON", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, "{0:N2} lei", true, "Romanian Leu", 8, "lei", null, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Currency",
                keyColumn: "Id",
                keyValue: 6L);

            migrationBuilder.DeleteData(
                table: "Currency",
                keyColumn: "Id",
                keyValue: 7L);

            migrationBuilder.DeleteData(
                table: "Currency",
                keyColumn: "Id",
                keyValue: 8L);
        }
    }
}
