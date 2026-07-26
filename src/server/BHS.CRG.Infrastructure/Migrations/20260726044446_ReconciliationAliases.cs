using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReconciliationAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reconciliation_aliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AliasKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CanonicalKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    AliasLabel = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CanonicalLabel = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ProposedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConfirmedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_aliases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_aliases_AliasKey",
                table: "reconciliation_aliases",
                column: "AliasKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reconciliation_aliases");
        }
    }
}
