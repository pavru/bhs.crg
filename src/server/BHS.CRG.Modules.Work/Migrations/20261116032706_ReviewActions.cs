using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class ReviewActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "comment",
                schema: "work",
                table: "report_journal",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "review_digest_retries",
                schema: "work",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_digest_retries", x => new { x.user_id, x.report_id, x.submitted_at });
                    table.ForeignKey(
                        name: "FK_review_digest_retries_reports_report_id",
                        column: x => x.report_id,
                        principalSchema: "work",
                        principalTable: "reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_digests",
                schema: "work",
                columns: table => new
                {
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    digested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_digests", x => new { x.report_id, x.submitted_at });
                    table.ForeignKey(
                        name: "FK_review_digests_reports_report_id",
                        column: x => x.report_id,
                        principalSchema: "work",
                        principalTable: "reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_digest_retries_report",
                schema: "work",
                table: "review_digest_retries",
                column: "report_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_digest_retries",
                schema: "work");

            migrationBuilder.DropTable(
                name: "review_digests",
                schema: "work");

            migrationBuilder.DropColumn(
                name: "comment",
                schema: "work",
                table: "report_journal");
        }
    }
}
