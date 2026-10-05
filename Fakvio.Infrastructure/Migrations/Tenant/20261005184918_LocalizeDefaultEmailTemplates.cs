using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <summary>
    /// Czech text for the default invoice / credit note / invitation / reminder e-mails (they were English under
    /// Language = "cs") plus English defaults for every e-mail type.
    ///
    /// Hand-written on purpose — the EF-generated UpdateData/InsertData by Id would overwrite templates users
    /// edited and could collide with tenant rows whose Ids were assigned by TenantProvisioningService.
    /// The SQL (shared with the Master migration) is in <see cref="LocalizeDefaultEmailTemplatesSql"/>; it uses
    /// unqualified table names, which resolve to the tenant schema through search_path during tenant migrations.
    /// </summary>
    public partial class LocalizeDefaultEmailTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var sql in LocalizeDefaultEmailTemplatesSql.Up())
                migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best effort: reverts only rows that still equal what Up() wrote; edited templates are left alone.
            foreach (var sql in LocalizeDefaultEmailTemplatesSql.Down())
                migrationBuilder.Sql(sql);
        }
    }
}
