using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddAiSystemPromptToSystemConfiguration_v118 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AiSystemPromptCustom: when non-empty, replaces the hardcoded style/rules sections
            // in ChatContextBuilder. SysAdmin can override the default assistant behavior here.
            migrationBuilder.AddColumn<string>(
                name: "AiSystemPromptCustom",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            // AiSystemPromptAppendix: optional additional rules appended AFTER the main prompt
            // (whether default or custom) and BEFORE the auto-generated business context block.
            migrationBuilder.AddColumn<string>(
                name: "AiSystemPromptAppendix",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiSystemPromptCustom",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiSystemPromptAppendix",
                table: "SystemConfiguration");
        }
    }
}
