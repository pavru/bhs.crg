using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Владелец-модуль, способ хранения, видимость и каналы чтения у типа (issue #955, ТЗ CORE-18,
    /// CORE-30, TYPE-5).
    ///
    /// ⚠️ Список кодов ядра ниже ЗАПИСАН ЗДЕСЬ ДОСЛОВНО, а не взят из <c>CoreOwnedTypes</c>,
    /// намеренно: миграция — история, а не поведение. Список типов ядра однажды изменится (первым
    /// же переносом справочников, задача G1), и миграция, читающая код, задним числом сменила бы
    /// смысл уже выполненного шага — на новых установках владельцы разошлись бы со старыми молча.
    /// Что две записи не разъехались, стережёт <c>CoreOwnedTypesMigrationTests</c>.
    ///
    /// Чем заполняются остальные три колонки — отдельное решение, и оно не «по умолчанию»:
    /// существующим типам записывается СОСТОЯНИЕ ДЕЛ, а не умолчание ТЗ. Сегодня объекты всех этих
    /// типов читают все каналы разом; запиши мы им «закрыто», признак лгал бы до самого дня, когда
    /// начнёт действовать (работа STG-11), — и в тот день половина системы погасла бы разом.
    /// Умолчание «закрыто» относится к типу, который объявит МОДУЛЬ, а таких ещё нет.
    /// </summary>
    public partial class TypeModuleOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Module",
                table: "document_types",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReadChannels",
                table: "document_types",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Storage",
                table: "document_types",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "document_types",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // Состояние дел на день перехода: объекты лежат в общей таблице и видны всем каналам.
            migrationBuilder.Sql("""
                update document_types
                   set "Storage" = 'SharedObject',
                       "Visibility" = 'Shared',
                       "ReadChannels" = '';
                """);

            // Типы, переходящие ЯДРУ (ТЗ CORE-30): организации, лица, стройки, единица измерения,
            // профили уровней — и всё, на что они опираются родителем или вложением.
            migrationBuilder.Sql("""
                update document_types set "Module" = 'core' where "Code" in (
                    'Организация',
                    'НаименованиеОрганизации',
                    'Адрес',
                    'ГеографическиеКоординаты',
                    'Угол',
                    'Персона',
                    'ФИО',
                    'ДополнительнаяХарактеристикаПерсоны',
                    'ОбъектСтроительства',
                    'ЕдиницаИзмерения',
                    'ПрофильСтройки',
                    'ПрофильРаздела'
                );
                """);

            // Всё остальное принадлежит исполнительной документации: до первого модуля системой
            // была она одна. Это исторический факт, а не умолчание для будущих типов.
            migrationBuilder.Sql("""update document_types set "Module" = 'id' where "Module" = '';""");

            migrationBuilder.CreateIndex(
                name: "IX_document_types_Module",
                table: "document_types",
                column: "Module");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_types_Module",
                table: "document_types");

            migrationBuilder.DropColumn(
                name: "Module",
                table: "document_types");

            migrationBuilder.DropColumn(
                name: "ReadChannels",
                table: "document_types");

            migrationBuilder.DropColumn(
                name: "Storage",
                table: "document_types");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "document_types");
        }
    }
}
