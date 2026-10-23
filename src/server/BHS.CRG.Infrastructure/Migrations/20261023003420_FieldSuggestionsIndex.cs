using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FieldSuggestionsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_domain_objects_CompositeTypeId_UpdatedAt_Live",
                table: "domain_objects",
                columns: new[] { "CompositeTypeId", "UpdatedAt" },
                descending: new[] { false, true },
                filter: "\"ArchivedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_domain_objects_CompositeTypeId_UpdatedAt_Live",
                table: "domain_objects");
        }
    }
}
