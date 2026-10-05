using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <summary>
    /// Czech text for the default invoice / credit note / invitation / reminder e-mails (they were English under
    /// Language = "cs") plus English defaults for every e-mail type.
    ///
    /// Hand-written on purpose — the EF-generated UpdateData/InsertData by Id would overwrite templates users
    /// edited and could collide with existing Ids. The SQL (shared with the Tenant migration) is in
    /// <see cref="LocalizeDefaultEmailTemplatesSql"/>; it only touches rows that are still the original seed.
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
