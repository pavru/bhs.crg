using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnifyCatalogScopeToString : Migration
    {
        // CatalogScope: Set = 1, Section = 2, Construction = 3, System = 5 (значение 4 не используется).
        private const string ScopeCaseToString =
            "CASE \"Scope\" WHEN 1 THEN 'Set' WHEN 2 THEN 'Section' WHEN 3 THEN 'Construction' WHEN 5 THEN 'System' END";

        private const string ScopeCaseToInt =
            "CASE \"Scope\" WHEN 'Set' THEN 1 WHEN 'Section' THEN 2 WHEN 'Construction' THEN 3 WHEN 'System' THEN 5 END";

        private static readonly string[] ScopeTables = ["quality_documents", "material_quality_links", "dataset_files", "common_data_entries"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in ScopeTables)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE {table} ALTER COLUMN \"Scope\" TYPE character varying(32) USING ({ScopeCaseToString});");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in ScopeTables)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE {table} ALTER COLUMN \"Scope\" TYPE integer USING ({ScopeCaseToInt});");
            }
        }
    }
}
