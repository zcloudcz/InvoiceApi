using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class Add_RecognizedCounterparty_v53 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RecognizedCounterpartyId",
                table: "BankTransaction",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecognizedCounterparty",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CounterpartyAccount = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    VariableSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SpecificSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ConstantSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognizedCounterparty", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransaction_RecognizedCounterpartyId",
                table: "BankTransaction",
                column: "RecognizedCounterpartyId");

            migrationBuilder.CreateIndex(
                name: "IX_RecognizedCounterparty_IsActive",
                table: "RecognizedCounterparty",
                column: "IsActive");

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransaction_RecognizedCounterparty_RecognizedCounterpar~",
                table: "BankTransaction",
                column: "RecognizedCounterpartyId",
                principalTable: "RecognizedCounterparty",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankTransaction_RecognizedCounterparty_RecognizedCounterpar~",
                table: "BankTransaction");

            migrationBuilder.DropTable(
                name: "RecognizedCounterparty");

            migrationBuilder.DropIndex(
                name: "IX_BankTransaction_RecognizedCounterpartyId",
                table: "BankTransaction");

            migrationBuilder.DropColumn(
                name: "RecognizedCounterpartyId",
                table: "BankTransaction");
        }
    }
}
