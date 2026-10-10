using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Renditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "renditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalBlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Mime = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ImageBlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Pages = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<List<string>>(type: "text[]", nullable: false),
                    RefusalKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RefusalReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Converter = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_renditions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_renditions_OriginalBlobPath",
                table: "renditions",
                column: "OriginalBlobPath",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "renditions");
        }
    }
}
