using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddProformaDocumentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "NumberSequence",
                columns: new[] { "Id", "CreatedAt", "CreatedByUserId", "CurrentMonth", "CurrentNumber", "CurrentYear", "DocumentType", "IsActive", "IsDefault", "Name", "NumberSequenceFormatId", "Prefix", "Suffix", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { 3L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, 0, null, 3, true, true, "Default Proforma Sequence", 1L, "PF", null, null, null },
                    { 4L, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, 0, null, 4, true, true, "Default Tax Receipt for Advance Sequence", 1L, "ZF", null, null, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "NumberSequence",
                keyColumn: "Id",
                keyValue: 3L);

            migrationBuilder.DeleteData(
                table: "NumberSequence",
                keyColumn: "Id",
                keyValue: 4L);
        }
    }
}
