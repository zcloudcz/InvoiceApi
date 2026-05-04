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
            // Use ON CONFLICT DO NOTHING — tenant may already have these rows
            // (e.g., inserted by TenantProvisioningService during initial setup).
            // Original InsertData with hardcoded Ids caused PK_NumberSequence violation.
            migrationBuilder.Sql(@"
                INSERT INTO ""NumberSequence""
                    (""Id"", ""CreatedAt"", ""CreatedByUserId"", ""CurrentMonth"", ""CurrentNumber"",
                     ""CurrentYear"", ""DocumentType"", ""IsActive"", ""IsDefault"", ""Name"",
                     ""NumberSequenceFormatId"", ""Prefix"", ""Suffix"", ""UpdatedAt"", ""UpdatedByUserId"")
                VALUES
                    (3, '2025-01-01T00:00:00Z', NULL, NULL, 0, NULL, 3, TRUE, TRUE,
                     'Default Proforma Sequence', 1, 'PF-', NULL, NULL, NULL),
                    (4, '2025-01-01T00:00:00Z', NULL, NULL, 0, NULL, 4, TRUE, TRUE,
                     'Default Tax Receipt for Advance Sequence', 1, 'DPP-', NULL, NULL, NULL)
                ON CONFLICT (""Id"") DO NOTHING;
            ");
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
