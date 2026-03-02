using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddLanguageToClientAndContentTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContentTemplate_TemplateType_IsDefault",
                schema: "tenant_template",
                table: "ContentTemplate");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                schema: "tenant_template",
                table: "ContentTemplate",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "cs");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                schema: "tenant_template",
                table: "Client",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "cs");

            migrationBuilder.UpdateData(
                schema: "tenant_template",
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 1L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                schema: "tenant_template",
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 2L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                schema: "tenant_template",
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 3L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                schema: "tenant_template",
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 4L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                schema: "tenant_template",
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 5L,
                column: "Language",
                value: "cs");

            migrationBuilder.UpdateData(
                schema: "tenant_template",
                table: "ContentTemplate",
                keyColumn: "Id",
                keyValue: 6L,
                column: "Language",
                value: "cs");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_TemplateType_Language_IsDefault",
                schema: "tenant_template",
                table: "ContentTemplate",
                columns: new[] { "TemplateType", "Language", "IsDefault" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContentTemplate_TemplateType_Language_IsDefault",
                schema: "tenant_template",
                table: "ContentTemplate");

            migrationBuilder.DropColumn(
                name: "Language",
                schema: "tenant_template",
                table: "ContentTemplate");

            migrationBuilder.DropColumn(
                name: "Language",
                schema: "tenant_template",
                table: "Client");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTemplate_TemplateType_IsDefault",
                schema: "tenant_template",
                table: "ContentTemplate",
                columns: new[] { "TemplateType", "IsDefault" });
        }
    }
}
