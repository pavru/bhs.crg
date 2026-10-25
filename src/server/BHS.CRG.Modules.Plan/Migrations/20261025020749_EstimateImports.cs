using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class EstimateImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "estimate_imports",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    estimate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_estimate_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    new_estimate_section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_estimate_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    blob_path = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    program_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    schema_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    lsr_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    lsr_name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    parsed = table.Column<string>(type: "jsonb", nullable: true),
                    unparsed = table.Column<string>(type: "jsonb", nullable: true),
                    refusal_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    refusal_text = table.Column<string>(type: "text", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_name = table.Column<string>(type: "text", nullable: true),
                    parsed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_estimate_imports", x => x.id);
                    table.ForeignKey(
                        name: "FK_estimate_imports_estimate_revisions_revision_id",
                        column: x => x.revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_estimate_imports_estimates_estimate_id",
                        column: x => x.estimate_id,
                        principalSchema: "plan",
                        principalTable: "estimates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_estimate_imports_construction_id",
                schema: "plan",
                table: "estimate_imports",
                column: "construction_id");

            migrationBuilder.CreateIndex(
                name: "ix_estimate_imports_estimate_id",
                schema: "plan",
                table: "estimate_imports",
                column: "estimate_id",
                filter: "estimate_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_estimate_imports_new_estimate_section_id",
                schema: "plan",
                table: "estimate_imports",
                column: "new_estimate_section_id",
                filter: "new_estimate_section_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_estimate_imports_revision_id",
                schema: "plan",
                table: "estimate_imports",
                column: "revision_id",
                filter: "revision_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "estimate_imports",
                schema: "plan");
        }
    }
}
