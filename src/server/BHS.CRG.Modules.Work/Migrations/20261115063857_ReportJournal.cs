using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class ReportJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "submitted_at",
                schema: "work",
                table: "reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "report_journal",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    executor_employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    executor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    entered_for = table.Column<bool>(type: "boolean", nullable: false),
                    device_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    server_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    after_submission = table.Column<bool>(type: "boolean", nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_journal", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_report_journal_report_seq",
                schema: "work",
                table: "report_journal",
                columns: new[] { "report_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_journal",
                schema: "work");

            migrationBuilder.DropColumn(
                name: "submitted_at",
                schema: "work",
                table: "reports");
        }
    }
}
