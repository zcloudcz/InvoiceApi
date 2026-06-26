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
            // Existing integer values (if any) are cast to their string representation.
            migrationBuilder.Sql("""
                ALTER TABLE "Client" ALTER COLUMN "TaxRegime" TYPE character varying(30)
                    USING "TaxRegime"::character varying(30);

                ALTER TABLE "Client" ALTER COLUMN "ActivityType" TYPE character varying(30)
                    USING "ActivityType"::character varying(30);

                ALTER TABLE "Client" ALTER COLUMN "FlatRateBand" TYPE character varying(10)
                    USING "FlatRateBand"::character varying(10);
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
