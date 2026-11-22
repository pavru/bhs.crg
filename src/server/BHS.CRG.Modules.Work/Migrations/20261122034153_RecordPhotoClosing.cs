using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class RecordPhotoClosing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "declared_in_closing_id",
                schema: "work",
                table: "line_photos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "stored_in_closing_id",
                schema: "work",
                table: "line_photos",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "declared_in_closing_id",
                schema: "work",
                table: "line_photos");

            migrationBuilder.DropColumn(
                name: "stored_in_closing_id",
                schema: "work",
                table: "line_photos");
        }
    }
}
