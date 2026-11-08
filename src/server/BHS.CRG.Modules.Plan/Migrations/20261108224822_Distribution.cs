using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class Distribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "distribution_carry_over",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_row_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lsr_section_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    lsr_section_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    rate_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    number_in_section = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    source_volume = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    source_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    parts = table.Column<string>(type: "jsonb", nullable: false),
                    rule_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    rule_section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    row_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_volume = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    candidates = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    resolution = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_distribution_carry_over", x => x.id);
                    table.ForeignKey(
                        name: "FK_distribution_carry_over_estimate_revisions_revision_id",
                        column: x => x.revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_distribution_carry_over_estimate_rows_row_id",
                        column: x => x.row_id,
                        principalSchema: "plan",
                        principalTable: "estimate_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "distribution_parts",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_distribution_parts", x => x.id);
                    table.ForeignKey(
                        name: "FK_distribution_parts_estimate_revisions_revision_id",
                        column: x => x.revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_distribution_parts_estimate_rows_row_id",
                        column: x => x.row_id,
                        principalSchema: "plan",
                        principalTable: "estimate_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "distribution_rules",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lsr_section_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_distribution_rules", x => x.id);
                    table.ForeignKey(
                        name: "FK_distribution_rules_estimate_revisions_revision_id",
                        column: x => x.revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "revision_distributions",
                schema: "plan",
                columns: table => new
                {
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    estimate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_by_name = table.Column<string>(type: "text", nullable: true),
                    carried_from_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    carried_from_number = table.Column<int>(type: "integer", nullable: true),
                    carried_rows = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revision_distributions", x => x.revision_id);
                    table.ForeignKey(
                        name: "FK_revision_distributions_estimate_revisions_carried_from_revi~",
                        column: x => x.carried_from_revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_revision_distributions_estimate_revisions_revision_id",
                        column: x => x.revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_revision_distributions_estimates_estimate_id",
                        column: x => x.estimate_id,
                        principalSchema: "plan",
                        principalTable: "estimates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_distribution_carry_over_revision_id",
                schema: "plan",
                table: "distribution_carry_over",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_carry_over_row_id",
                schema: "plan",
                table: "distribution_carry_over",
                column: "row_id",
                filter: "row_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_parts_revision_id",
                schema: "plan",
                table: "distribution_parts",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_parts_section_id",
                schema: "plan",
                table: "distribution_parts",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "ux_distribution_parts_row_section",
                schema: "plan",
                table: "distribution_parts",
                columns: new[] { "row_id", "section_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_distribution_rules_section_id",
                schema: "plan",
                table: "distribution_rules",
                column: "section_id",
                filter: "section_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_distribution_rules_revision_section",
                schema: "plan",
                table: "distribution_rules",
                columns: new[] { "revision_id", "lsr_section_code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_revision_distributions_carried_from",
                schema: "plan",
                table: "revision_distributions",
                column: "carried_from_revision_id",
                filter: "carried_from_revision_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_revision_distributions_estimate_id",
                schema: "plan",
                table: "revision_distributions",
                column: "estimate_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "distribution_carry_over",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "distribution_parts",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "distribution_rules",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "revision_distributions",
                schema: "plan");
        }
    }
}
