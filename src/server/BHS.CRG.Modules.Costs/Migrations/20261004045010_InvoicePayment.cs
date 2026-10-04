using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoicePayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // До этой миграции отметить оплату было нечем: в колонке может стоять только «Unpaid».
            // «Partial» из перечисления убран (решение владельца 04.10.2026), а «Paid» без даты платежа
            // нарушил бы ограничение ниже. Нашлось иное — останавливаемся и говорим, что именно: строку
            // поставили руками, и угадывать за неё дату платежа миграция не вправе.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE found text;
                BEGIN
                    SELECT string_agg(DISTINCT payment, ', ') INTO found
                    FROM costs.invoices WHERE payment <> 'Unpaid';
                    IF found IS NOT NULL THEN
                        RAISE EXCEPTION 'costs.invoices: состояние оплаты «%» у счетов, оплату которых отметить было нечем. Миграция оплаты счёта (C5) не знает ни даты платежа, ни учётных дат таких счетов. Верните им состояние Unpaid и отметьте оплату после обновления.', found;
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paid_at",
                schema: "costs",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "paid_by",
                schema: "costs",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "paid_on",
                schema: "costs",
                table: "invoices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_document",
                schema: "costs",
                table: "invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "remainder_accounting_on",
                schema: "costs",
                table: "invoices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "accounting_on",
                schema: "costs",
                table: "invoice_allocations",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_paid_on",
                schema: "costs",
                table: "invoices",
                column: "paid_on",
                filter: "paid_on IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoices_paid_on",
                schema: "costs",
                table: "invoices",
                sql: "(payment = 'Paid') = (paid_on IS NOT NULL) AND (payment = 'Paid' OR remainder_accounting_on IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_allocations_accounting",
                schema: "costs",
                table: "invoice_allocations",
                column: "accounting_on",
                filter: "accounting_on IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_paid_on",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoices_paid_on",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_invoice_allocations_accounting",
                schema: "costs",
                table: "invoice_allocations");

            migrationBuilder.DropColumn(
                name: "paid_at",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "paid_by",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "paid_on",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "payment_document",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "remainder_accounting_on",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "accounting_on",
                schema: "costs",
                table: "invoice_allocations");
        }
    }
}
