using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class LinePhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "line_photo_removals",
                schema: "work",
                columns: table => new
                {
                    photo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_line_photo_removals", x => x.photo_id);
                    table.ForeignKey(
                        name: "FK_line_photo_removals_report_lines_line_id",
                        column: x => x.line_id,
                        principalSchema: "work",
                        principalTable: "report_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "line_photos",
                schema: "work",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    blob_path = table.Column<string>(type: "text", nullable: true),
                    preview_blob_path = table.Column<string>(type: "text", nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    geo_lat = table.Column<double>(type: "double precision", nullable: true),
                    geo_lon = table.Column<double>(type: "double precision", nullable: true),
                    geo_alt = table.Column<double>(type: "double precision", nullable: true),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    declared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stored_seq = table.Column<long>(type: "bigint", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_line_photos", x => x.id);
                    table.CheckConstraint("ck_line_photos_state", "state IN ('pending', 'stored')");
                    table.CheckConstraint("ck_line_photos_stored", "state = 'pending' OR (blob_path IS NOT NULL AND preview_blob_path IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_line_photos_report_lines_line_id",
                        column: x => x.line_id,
                        principalSchema: "work",
                        principalTable: "report_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_line_photo_removals_line",
                schema: "work",
                table: "line_photo_removals",
                column: "line_id");

            migrationBuilder.CreateIndex(
                name: "ix_line_photos_line",
                schema: "work",
                table: "line_photos",
                column: "line_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "line_photo_removals",
                schema: "work");

            migrationBuilder.DropTable(
                name: "line_photos",
                schema: "work");
        }
    }
}
