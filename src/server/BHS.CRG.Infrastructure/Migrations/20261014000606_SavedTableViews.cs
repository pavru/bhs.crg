using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SavedTableViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "table_views",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TableAddress = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BasedOn = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Shared = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_table_views", x => x.Id);
                    table.ForeignKey(
                        name: "FK_table_views_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_table_views_own_title",
                table: "table_views",
                columns: new[] { "TableAddress", "OwnerId", "Title" },
                unique: true,
                filter: "NOT \"Shared\"");

            migrationBuilder.CreateIndex(
                name: "IX_table_views_OwnerId",
                table: "table_views",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_table_views_shared_title",
                table: "table_views",
                columns: new[] { "TableAddress", "Title" },
                unique: true,
                filter: "\"Shared\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "table_views");
        }
    }
}
