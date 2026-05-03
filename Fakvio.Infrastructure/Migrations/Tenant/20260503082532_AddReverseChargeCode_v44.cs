using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddReverseChargeCode_v44 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create the ReverseChargeCode lookup table.
            // Stores MFČR reverse charge codes (kódy předmětu plnění PDP) for VAT control statement
            // (kontrolní hlášení / EPO XML) sections A.1 and B.1.
            migrationBuilder.CreateTable(
                name: "ReverseChargeCode",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    NameCs = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ParagraphRef = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReverseChargeCode", x => x.Id);
                });

            // Seed all 14 MFČR codes (Příloha č. 6 ZDPH + GFŘ D-59).
            // ValidFrom = 2016-01-01 — when §92e and the current číselník took effect.
            // ValidTo = null — codes remain valid until MFČR publishes a new revision.
            migrationBuilder.InsertData(
                table: "ReverseChargeCode",
                columns: new[] { "Id", "Code", "CreatedAt", "CreatedByUserId", "IsActive", "NameCs", "NameEn", "ParagraphRef", "UpdatedAt", "UpdatedByUserId", "ValidFrom", "ValidTo" },
                values: new object[,]
                {
                    // §92b — gold
                    { 1L,  "1",  new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Zlato",                                                                      "Gold",                                                                      "§92b", null, null, new DateOnly(2016, 1, 1), null },
                    { 2L,  "1a", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Investiční zlato",                                                           "Investment gold",                                                           "§92b", null, null, new DateOnly(2016, 1, 1), null },
                    // §92c — waste, scrap, mobile devices, CPUs, emission allowances
                    { 3L,  "3",  new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Odpady a šrot",                                                              "Waste and scrap",                                                           "§92c", null, null, new DateOnly(2016, 1, 1), null },
                    { 4L,  "3a", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Emisní povolenky",                                                           "Greenhouse gas emission allowances",                                        "§92c", null, null, new DateOnly(2016, 1, 1), null },
                    { 5L,  "4",  new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Obiloviny a technické plodiny",                                              "Cereals and industrial crops",                                              "§92c", null, null, new DateOnly(2016, 1, 1), null },
                    { 6L,  "5",  new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Mobilní telefony",                                                           "Mobile phones",                                                             "§92c", null, null, new DateOnly(2016, 1, 1), null },
                    { 7L,  "6",  new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Integrované obvody a desky plošných spojů",                                  "Integrated circuits and printed circuit boards",                            "§92c", null, null, new DateOnly(2016, 1, 1), null },
                    { 8L,  "7",  new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Přenosná zařízení pro automatické zpracování dat (laptopy, tablety apod.)",  "Portable automatic data-processing devices (laptops etc.)",                 "§92c", null, null, new DateOnly(2016, 1, 1), null },
                    // §92d — construction and assembly work
                    { 9L,  "11", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Stavební nebo montážní práce",                                               "Construction or assembly work",                                             "§92d", null, null, new DateOnly(2016, 1, 1), null },
                    // §92e — transfer of emission allowances + other special supplies
                    { 10L, "12", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Převod povolenek na emise skleníkových plynů",                               "Transfer of greenhouse gas emission allowances",                            "§92e", null, null, new DateOnly(2016, 1, 1), null },
                    { 11L, "13", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Dodání elektřiny obchodníkovi",                                              "Supply of electricity to a trader",                                         "§92e", null, null, new DateOnly(2016, 1, 1), null },
                    { 12L, "14", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Dodání plynu obchodníkovi",                                                  "Supply of gas to a trader",                                                 "§92e", null, null, new DateOnly(2016, 1, 1), null },
                    { 13L, "21", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Poskytnutí pracovní síly v oblasti stavebnictví",                            "Provision of labour in construction",                                       "§92e", null, null, new DateOnly(2016, 1, 1), null },
                    { 14L, "25", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Dodání nemovité věci, pokud se plátce rozhodl uplatnit daň",                 "Supply of immovable property where the taxable person opted to tax",         "§92e", null, null, new DateOnly(2016, 1, 1), null }
                });

            // Unique index on Code — each MFČR code string appears at most once.
            migrationBuilder.CreateIndex(
                name: "IX_ReverseChargeCode_Code",
                table: "ReverseChargeCode",
                column: "Code",
                unique: true);

            // Index on IsActive — supports the GetAllActiveAsync hot path (WHERE IsActive = true).
            migrationBuilder.CreateIndex(
                name: "IX_ReverseChargeCode_IsActive",
                table: "ReverseChargeCode",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReverseChargeCode");
        }
    }
}
