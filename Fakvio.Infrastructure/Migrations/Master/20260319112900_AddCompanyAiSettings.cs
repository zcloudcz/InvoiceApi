using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddCompanyAiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiClaudeApiKey",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiClaudeModel",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiDefaultProvider",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiGeminiApiKey",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiGeminiModel",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiOllamaBaseUrl",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiOllamaModel",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiOpenAiApiKey",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiOpenAiModel",
                table: "CompanySystemSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiClaudeApiKey",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "AiClaudeModel",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "AiDefaultProvider",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "AiGeminiApiKey",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "AiGeminiModel",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "AiOllamaBaseUrl",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "AiOllamaModel",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "AiOpenAiApiKey",
                table: "CompanySystemSettings");

            migrationBuilder.DropColumn(
                name: "AiOpenAiModel",
                table: "CompanySystemSettings");
        }
    }
}
