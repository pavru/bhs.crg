using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BlobRegistryOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Owner",
                table: "blob_registry",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                // Умолчание базы остаётся и после миграции (модель его не знает, EF пишет владельца
                // сам): им получают владельца строки, лежавшие до колонки, и пути, которые вставляет
                // SQL сбора реестра #672 (BlobRegistryBackfill).
                defaultValue: BHS.CRG.Domain.Storage.BlobOwner.Core);

            // Файлы модулей — их модулям (ТЗ CORE-37.2). Миграцией, то есть РОВНО ОДИН раз: путь,
            // вписанный клиентом в поле, повторный проход отдал бы модулю. Причины — у
            // BlobRegistryOwnerBackfill. Схемы модулей к этому моменту уже есть у той базы, где есть
            // их файлы: миграции модулей применялись прежними стартами.
            migrationBuilder.Sql(BHS.CRG.Infrastructure.Maintenance.BlobRegistryOwnerBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Owner",
                table: "blob_registry");
        }
    }
}
