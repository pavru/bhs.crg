using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class ReportEnteredBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "entered_by_name",
                schema: "work",
                table: "reports",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "entered_by_user_id",
                schema: "work",
                table: "reports",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "entered_by_name",
                schema: "work",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "entered_by_user_id",
                schema: "work",
                table: "reports");
        }
    }
}
