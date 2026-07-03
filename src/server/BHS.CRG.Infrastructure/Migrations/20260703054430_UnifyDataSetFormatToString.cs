using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnifyDataSetFormatToString : Migration
    {
        // DataSetFormat (без явных int-значений, порядок членов подтверждён по истории миграций —
        // Pdf дописан в конец 2026-07-02, остальные не переставлялись):
        // Csv=0, Xlsx=1, Xls=2, Xml=3, Json=4, Zip=5, Pdf=6.
        private const string FormatCaseToString =
            "CASE \"Format\" WHEN 0 THEN 'Csv' WHEN 1 THEN 'Xlsx' WHEN 2 THEN 'Xls' WHEN 3 THEN 'Xml' " +
            "WHEN 4 THEN 'Json' WHEN 5 THEN 'Zip' WHEN 6 THEN 'Pdf' END";

        private const string FormatCaseToInt =
            "CASE \"Format\" WHEN 'Csv' THEN 0 WHEN 'Xlsx' THEN 1 WHEN 'Xls' THEN 2 WHEN 'Xml' THEN 3 " +
            "WHEN 'Json' THEN 4 WHEN 'Zip' THEN 5 WHEN 'Pdf' THEN 6 END";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE dataset_files ALTER COLUMN \"Format\" TYPE character varying(16) USING ({FormatCaseToString});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE dataset_files ALTER COLUMN \"Format\" TYPE integer USING ({FormatCaseToInt});");
        }
    }
}
