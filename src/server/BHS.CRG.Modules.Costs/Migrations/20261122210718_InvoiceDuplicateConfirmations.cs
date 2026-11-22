using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceDuplicateConfirmations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "duplicate_revision",
                schema: "costs",
                table: "invoices",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateTable(
                name: "invoice_duplicate_confirmations",
                schema: "costs",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_revision = table.Column<Guid>(type: "uuid", nullable: false),
                    second_invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    second_revision = table.Column<Guid>(type: "uuid", nullable: false),
                    first_label = table.Column<string>(type: "text", nullable: false),
                    second_label = table.Column<string>(type: "text", nullable: false),
                    author_id = table.Column<string>(type: "text", nullable: false),
                    author_name = table.Column<string>(type: "text", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    journaled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_duplicate_confirmations", x => x.event_id);
                    table.CheckConstraint("ck_invoice_duplicate_order", "first_invoice_id < second_invoice_id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_duplicate_pair",
                schema: "costs",
                table: "invoice_duplicate_confirmations",
                columns: new[] { "first_invoice_id", "first_revision", "second_invoice_id", "second_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoice_duplicate_pending",
                schema: "costs",
                table: "invoice_duplicate_confirmations",
                column: "confirmed_at",
                filter: "journaled_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_duplicate_second",
                schema: "costs",
                table: "invoice_duplicate_confirmations",
                columns: new[] { "second_invoice_id", "second_revision" });

            // Восстановление старой копии пишет только известные ей колонки. Если редакция не пришла
            // с записью, пересчитываем её по изменённой строке и проверяем отпечатки обоих путей.
            migrationBuilder.Sql("""
                CREATE FUNCTION costs.update_invoice_duplicate_revision() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.duplicate_revision IS DISTINCT FROM OLD.duplicate_revision THEN
                        RETURN NEW;
                    END IF;

                    IF ROW(NEW.supplier_id, NEW.number, NEW.issued_on)
                          IS DISTINCT FROM ROW(OLD.supplier_id, OLD.number, OLD.issued_on)
                      OR (NEW.scan_blob_path IS DISTINCT FROM OLD.scan_blob_path AND NOT COALESCE(
                          NEW.scan_fingerprint = OLD.scan_fingerprint
                          AND NEW.scan_fingerprint_seal = encode(sha256(convert_to(NEW.scan_blob_path, 'UTF8')), 'hex')
                          AND OLD.scan_fingerprint_seal = encode(sha256(convert_to(OLD.scan_blob_path, 'UTF8')), 'hex'),
                          false)) THEN
                        NEW.duplicate_revision := gen_random_uuid();
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER invoice_duplicate_revision BEFORE UPDATE ON costs.invoices
                    FOR EACH ROW EXECUTE FUNCTION costs.update_invoice_duplicate_revision();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER invoice_duplicate_revision ON costs.invoices;
                DROP FUNCTION costs.update_invoice_duplicate_revision();
                """);
            migrationBuilder.DropTable(
                name: "invoice_duplicate_confirmations",
                schema: "costs");

            migrationBuilder.DropColumn(
                name: "duplicate_revision",
                schema: "costs",
                table: "invoices");
        }
    }
}
