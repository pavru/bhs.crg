using BHS.CRG.Domain.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class DomainObjectConfiguration : IEntityTypeConfiguration<DomainObject>
{
    /// <summary>
    /// Колонка, по которой ищет выбор «по подстроке» (issue #1247): название и альтернативные имена
    /// одной строкой. Свойства у сущности нет — колонку считает база, читает её только запрос.
    /// </summary>
    public const string SearchText = "SearchText";

    /// <summary>
    /// Функция базы, собирающая текст поиска. Заводит и снимает её миграция <c>ChoiceSearchIndex</c>.
    ///
    /// <para>Обёртка нужна потому, что <c>array_to_string</c> помечена STABLE и в генерируемую
    /// колонку напрямую не годится. Тело — в стандартной форме (<c>RETURN …</c>): разбирается при
    /// создании и от <c>search_path</c> вызывающего не зависит.</para>
    /// </summary>
    public const string SearchTextFunction = "choice_search_text";

    public void Configure(EntityTypeBuilder<DomainObject> b)
    {
        b.ToTable("domain_objects");
        b.HasKey(e => e.Id);
        b.Property(e => e.DisplayName).HasMaxLength(512);
        // Алиасы (issue #74) — Postgres text[]. Сопоставление по имени идёт в памяти; поиск по
        // подстроке — по колонке ниже.
        b.Property(e => e.Aliases).HasColumnType("text[]").IsRequired();

        // Текст поиска «на выбор» (issue #1247) — ГЕНЕРИРУЕМАЯ колонка, а не та, что пишет
        // приложение: разойтись с названием и именами она не может. Устаревший текст поиска — это
        // «позиция не нашлась → завели дубль» (случай #1169), отказ, переодетый в результат.
        // Хранимая обязательно: в PostgreSQL 18 без STORED колонка виртуальная и не индексируется.
        // Разделитель — перевод строки; в набранном его заменяет пробелом сам поиск, иначе образец
        // совпал бы через границу двух имён.
        //
        // Цена принята осознанно (ревью PR #1307): свойство в модели — значит, колонку читает
        // каждый запрос объекта и возвращает каждая запись. Это копия названия и имён, десятки
        // байт рядом с Data, где лежат картинки. Убрать её из модели значило бы завести колонку и
        // индекс сырым SQL мимо снимка модели и искать через соединение таблицы с самой собой.
        b.Property<string>(SearchText)
            .HasComputedColumnSql($"{SearchTextFunction}(\"DisplayName\", \"Aliases\")", stored: true);
        // Индекс полный, без условия: «только общие данные» — это отсутствие фасеты, а она в другой
        // таблице; «не в архиве» нельзя — архив под запрос считается тем же отбором. Регистр
        // сворачивает сам ILIKE: gin_trgm_ops его поддерживает, а lower() с двух сторон дал бы
        // второе выражение, обязанное совпадать с индексом буква в букву.
        b.HasIndex(SearchText).HasMethod("gin").HasOperators("gin_trgm_ops");
        b.Property(e => e.CompositeTypeId).IsRequired();
        b.Property(e => e.Data).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.ScopeLevel).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.ScopeId);
        b.HasIndex(e => new { e.ScopeLevel, e.ScopeId });
        b.HasIndex(e => e.CompositeTypeId);
        // Подсчёт «в архиве» идёт вторым запросом при каждом поиске и при открытии выбора — с пустым
        // образцом триграммы ему не помогут. Архивных мало, отдельный индекс по ним почти ничего не
        // стоит записи.
        b.HasIndex(e => e.CompositeTypeId, "IX_domain_objects_CompositeTypeId_Archived")
            .HasFilter("\"ArchivedAt\" IS NOT NULL");

        // Признак архива (issue #1185) обычное сохранение НЕ пишет: после вставки колонка для него
        // не существует. Запись сохраняется целиком (Repository.Update — это DbSet.Update, а
        // восстановление копии ставит EntityState.Modified на свежую сущность), и без этого любая
        // правка, прочитанная до архива, а сохранённая после, сняла бы архив молча. Меняет колонку
        // одна служба — RecordArchive, условным ExecuteUpdate.
        b.Property(e => e.ArchivedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        // Версия строки (issue #1214) — системная колонка xmin: колонки в таблице не прибавляется,
        // миграция только сообщает модели, что свойство есть.
        //
        // ⚠️ НЕ токен конкурентности (не IsRowVersion), и это решение, а не недосмотр. Токен действует
        // на модель целиком: каждая запись строки объекта — документа тоже, и из фоновой работы —
        // стала бы условной, и там, где сегодня побеждает последний, появился бы отказ посреди
        // выпуска или пакетной сверки. Версию сверяет тот, кому её назвали, — правка записи общих
        // данных, под блокировкой строки (DomainObjectRepository.SaveSeenAsync).
        b.Property(e => e.RowVersion).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate();

        // Passthrough-коллекция документной фасеты — НЕ навигация объекта (иначе EF заведёт лишний
        // теневой FK generated_files.DomainObjectId). Файлы висят на фасете (см. DocumentFacetConfiguration).
        b.Ignore(e => e.GeneratedFiles);

        // Документная фасета — 1:1 dependent (есть ⟺ объект-документ), каскад при удалении объекта.
        b.HasOne(e => e.Facet)
            .WithOne()
            .HasForeignKey<DocumentFacet>(f => f.ObjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class DocumentFacetConfiguration : IEntityTypeConfiguration<DocumentFacet>
{
    public void Configure(EntityTypeBuilder<DocumentFacet> b)
    {
        b.ToTable("document_facets");
        b.HasKey(e => e.ObjectId);
        b.Property(e => e.ObjectId).ValueGeneratedNever();
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.PluginData).HasColumnType("jsonb");
        b.Property(e => e.TemplateParams).HasColumnType("jsonb");
        b.Property(e => e.TemplateIds).HasColumnType("jsonb");
        b.HasMany(e => e.GeneratedFiles)
            .WithOne()
            .HasForeignKey(f => f.ObjectId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(e => e.GeneratedFiles)
            .HasField("Files")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
