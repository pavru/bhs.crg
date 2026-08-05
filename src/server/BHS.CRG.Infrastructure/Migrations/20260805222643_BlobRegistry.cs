using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BlobRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blob_registry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MimeType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blob_registry", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blob_registry_Path",
                table: "blob_registry",
                column: "Path",
                unique: true);

            // Сбор реестра по данным, накопленным ДО появления проверки выдачи (issue #672). Без
            // него первый же запуск на существующей базе перестал бы отдавать все ранее загруженные
            // файлы: проверка отказывает всему, чего в реестре нет.
            //
            // Именно миграцией, то есть РОВНО ОДИН раз. Пути опознаются по форме строки, а JSONB
            // реквизитов пишет клиент — повторяющийся проход означал бы, что вписанная в поле строка
            // нужного вида попадает в реестр после перезапуска, и проверка стала бы значить «строка
            // совпала с выражением» вместо «объект создали мы». Ходить в блоб-хранилище здесь не
            // нужно (в отличие от переноса картинок #522), поэтому миграция уместна: база к этому
            // моменту заведомо есть.
            migrationBuilder.Sql(BHS.CRG.Infrastructure.Maintenance.BlobRegistryBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blob_registry");
        }
    }
}
