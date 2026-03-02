using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddLanguageToClientAndContentTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContentTemplate_TemplateType_IsDefault",
                table: "ContentTemplate");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "ContentTemplate",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "cs");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Client",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "cs");

            migrationBuilder.UpdateData(
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 1L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 2L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 3L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 4L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 5L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 6L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 7L,
                column: "Language",
                value: "cs");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_TemplateType_Language_IsDefault",
                table: "ContentTemplate",
                columns: new[] { "TemplateType", "Language", "IsDefault" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContentTemplate_TemplateType_Language_IsDefault",
                table: "ContentTemplate");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "ContentTemplate");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Client");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_TemplateType_IsDefault",
                table: "ContentTemplate",
                columns: new[] { "TemplateType", "IsDefault" });
        }
    }
}
