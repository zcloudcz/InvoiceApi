using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class FixClientEnumColumnsToString_v48 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL requires USING clause when changing column type from integer to varchar.
            // Explicit CASE mapping converts integer enum values to their C# name strings,
            // matching what EF Core HasConversion<string?>() expects.
            migrationBuilder.Sql("""
                ALTER TABLE "Client" ALTER COLUMN "TaxRegime" TYPE character varying(30)
                    USING CASE "TaxRegime"
                        WHEN 1 THEN 'FlatRateTax'
                        WHEN 2 THEN 'LumpSumExpenses80'
                        WHEN 3 THEN 'LumpSumExpenses60'
                        WHEN 4 THEN 'LumpSumExpenses40'
                        WHEN 5 THEN 'LumpSumExpenses30'
                        WHEN 6 THEN 'TaxRecords'
                        WHEN 7 THEN 'FullAccounting'
                        ELSE NULL
                    END;

                ALTER TABLE "Client" ALTER COLUMN "ActivityType" TYPE character varying(30)
                    USING CASE "ActivityType"
                        WHEN 1 THEN 'CraftTrade'
                        WHEN 2 THEN 'NonCraftTrade'
                        WHEN 3 THEN 'RegulatedProfession'
                        WHEN 4 THEN 'Rental'
                        ELSE NULL
                    END;

                ALTER TABLE "Client" ALTER COLUMN "FlatRateBand" TYPE character varying(10)
                    USING CASE "FlatRateBand"
                        WHEN 1 THEN 'Band1'
                        WHEN 2 THEN 'Band2'
                        WHEN 3 THEN 'Band3'
                        ELSE NULL
                    END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "TaxRegime",
                table: "Client",
                type: "integer",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "FlatRateBand",
                table: "Client",
                type: "integer",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ActivityType",
                table: "Client",
                type: "integer",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30,
                oldNullable: true);
        }
    }
}
