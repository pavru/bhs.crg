using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <summary>
    /// Строки счёта (задача C2 этапа 2, issue #1078, ТЗ COST-7): своя таблица в схеме модуля.
    ///
    /// <para>Внешний ключ на счёт есть — он ВНУТРИ схемы модуля, а запрещены только сквозные, между
    /// схемой модуля и схемой ядра (A2a, issue #1072). Ссылка на позицию номенклатуры ядра — наоборот,
    /// идентификатором без ключа, и целость её проверяется чтением.</para>
    ///
    /// <para>Частичный индекс «ждут позиции» — под отбор «Разобрать»: строк с позицией со временем
    /// станет подавляющее большинство, и держать их в этом индексе незачем.</para>
    /// </summary>
    public partial class InvoiceLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    nomenclature_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_text = table.Column<string>(type: "text", nullable: true),
                    supplier_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    vat_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    vat_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "costs",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_order",
                schema: "costs",
                table: "invoice_lines",
                columns: new[] { "invoice_id", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_unmatched",
                schema: "costs",
                table: "invoice_lines",
                column: "invoice_id",
                filter: "nomenclature_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "costs");
        }
    }
}
