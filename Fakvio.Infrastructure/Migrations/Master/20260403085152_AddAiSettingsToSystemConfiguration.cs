using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddAiSettingsToSystemConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiClaudeApiKey",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiClaudeModel",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiDefaultProvider",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiGeminiApiKey",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiGeminiModel",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiOllamaBaseUrl",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiOllamaModel",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiOpenAiApiKey",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiOpenAiModel",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiClaudeApiKey",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiClaudeModel",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiDefaultProvider",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiGeminiApiKey",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiGeminiModel",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiOllamaBaseUrl",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiOllamaModel",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiOpenAiApiKey",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiOpenAiModel",
                table: "SystemConfiguration");
        }
    }
}
