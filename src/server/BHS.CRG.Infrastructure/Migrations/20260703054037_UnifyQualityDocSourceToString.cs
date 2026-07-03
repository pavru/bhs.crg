using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnifyQualityDocSourceToString : Migration
    {
        // QualityDocSource: Manual = 0, Fgis = 1, Manufacturer = 2, Web = 3.
        private const string SourceCaseToString =
            "CASE \"Source\" WHEN 0 THEN 'Manual' WHEN 1 THEN 'Fgis' WHEN 2 THEN 'Manufacturer' WHEN 3 THEN 'Web' END";

        private const string SourceCaseToInt =
            "CASE \"Source\" WHEN 'Manual' THEN 0 WHEN 'Fgis' THEN 1 WHEN 'Manufacturer' THEN 2 WHEN 'Web' THEN 3 END";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE quality_documents ALTER COLUMN \"Source\" TYPE character varying(32) USING ({SourceCaseToString});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE quality_documents ALTER COLUMN \"Source\" TYPE integer USING ({SourceCaseToInt});");
        }
    }
}
