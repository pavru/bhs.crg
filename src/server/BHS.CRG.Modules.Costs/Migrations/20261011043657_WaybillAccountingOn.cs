using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class WaybillAccountingOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "accounting_on",
                schema: "costs",
                table: "waybills",
                type: "date",
                nullable: true);

            // Уже проведённые накладные получают учётной датой дату отпуска (issue #1285): как их
            // относили к периодам до этой миграции, так и остаётся. Следствие названо в заметках к
            // выпуску: накладные закрытых месяцев с этого места заперты.
            migrationBuilder.Sql(
                "UPDATE costs.waybills SET accounting_on = issued_on WHERE state = 'Posted'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "accounting_on",
                schema: "costs",
                table: "waybills");
        }
    }
}
