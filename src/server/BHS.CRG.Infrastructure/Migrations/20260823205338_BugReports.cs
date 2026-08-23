using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BugReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bug_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    TechContext = table.Column<string>(type: "text", nullable: true),
                    ScreenshotBlobPath = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IssueDraft = table.Column<string>(type: "text", nullable: true),
                    GithubIssueNumber = table.Column<int>(type: "integer", nullable: true),
                    GithubIssueUrl = table.Column<string>(type: "text", nullable: true),
                    FixedInVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bug_reports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bug_reports_AuthorId",
                table: "bug_reports",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_bug_reports_CreatedAt",
                table: "bug_reports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_bug_reports_Status",
                table: "bug_reports",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bug_reports");
        }
    }
}
