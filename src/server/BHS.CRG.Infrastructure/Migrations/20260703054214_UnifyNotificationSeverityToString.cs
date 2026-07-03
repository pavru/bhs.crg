using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnifyNotificationSeverityToString : Migration
    {
        // NotificationSeverity: Info = 0, Warning = 1, Error = 2.
        private const string SeverityCaseToString =
            "CASE \"Severity\" WHEN 0 THEN 'Info' WHEN 1 THEN 'Warning' WHEN 2 THEN 'Error' END";

        private const string SeverityCaseToInt =
            "CASE \"Severity\" WHEN 'Info' THEN 0 WHEN 'Warning' THEN 1 WHEN 'Error' THEN 2 END";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE notifications ALTER COLUMN \"Severity\" TYPE character varying(16) USING ({SeverityCaseToString});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE notifications ALTER COLUMN \"Severity\" TYPE integer USING ({SeverityCaseToInt});");
        }
    }
}
