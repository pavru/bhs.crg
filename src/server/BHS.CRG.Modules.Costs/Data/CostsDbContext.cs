using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Контекст модуля «Счета и накладные»: схема <c>costs</c>, свой набор миграций, своя история
/// (задача A2a этапа 2, issue #1072, ТЗ CORE-4).
///
/// <para>Первая таблица — счёт (C1, issue #1076): она приехала вместе со своим типом и адресами,
/// которые её читают, а не раньше них. Таблица, заведённая раньше потребителя, проверяется только
/// тем, что она создалась, — колонки у неё угаданы.</para>
///
/// <para>Начальная миграция при этом не пустая по смыслу: она создаёт саму схему и историю миграций
/// модуля в ней. Ровно это и включается на чистой и на рабочей базе, и ровно это переживает
/// выключение модуля.</para>
/// </summary>
public sealed class CostsDbContext(DbContextOptions<CostsDbContext> options) : ModuleDbContext(options)
{
    /// <summary>Имя схемы. То же, что объявлено модулем (<c>CostsModule.Schema</c>) — ядро сверяет.</summary>
    public const string SchemaName = "costs";

    protected override string Schema => SchemaName;

    /// <summary>
    /// Счета на оплату — записи модуля (ТЗ CORE-20.1). Дописываемым набором НЕ объявлены
    /// (<c>IAppendOnly</c>): черновик счёта правят, и правят много раз — это его штатная жизнь.
    /// Неизменяемыми будут версии записей (E2, issue #1100), а не сами счета.
    /// </summary>
    public DbSet<Invoice> Invoices => Set<Invoice>();

    /// <summary>
    /// Раскладка таблицы счёта. Вручную, а не соглашениями EF: имена таблиц и колонок здесь — это
    /// то, что увидит человек в отчёте резервной копии и в запросе к базе, а соглашение по умолчанию
    /// дало бы «Invoices» и «IssuedOn» в схеме, где всё остальное названо по-русски и через
    /// подчёркивание.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var invoice = builder.Entity<Invoice>();
        invoice.ToTable("invoices");
        invoice.HasKey(i => i.Id);

        invoice.Property(i => i.Id).HasColumnName("id");
        invoice.Property(i => i.DocumentTypeId).HasColumnName("document_type_id");
        invoice.Property(i => i.Number).HasColumnName("number").HasMaxLength(100);
        invoice.Property(i => i.IssuedOn).HasColumnName("issued_on");
        invoice.Property(i => i.SupplierId).HasColumnName("supplier_id");
        invoice.Property(i => i.PayerId).HasColumnName("payer_id");
        invoice.Property(i => i.Purpose).HasColumnName("purpose");

        // Деньги — numeric(18,2), а не double: копейка, потерянная при округлении двоичной дроби,
        // расходится с бумагой поставщика, и расхождение это не воспроизводится на глаз.
        invoice.Property(i => i.Total).HasColumnName("total").HasPrecision(18, 2);
        invoice.Property(i => i.VatTotal).HasColumnName("vat_total").HasPrecision(18, 2);

        invoice.Property(i => i.ShippedOn).HasColumnName("shipped_on");
        invoice.Property(i => i.DeferralDays).HasColumnName("deferral_days");
        invoice.Property(i => i.DueDate).HasColumnName("due_date");
        invoice.Property(i => i.DueDateManual).HasColumnName("due_date_manual");

        // Состояния — строками, а не числами перечисления. Число в базе читается только вместе с
        // кодом, а в эту таблицу будут смотреть и запросом, и отчётом копии, и выгрузкой.
        invoice.Property(i => i.State).HasColumnName("state").HasConversion<string>().HasMaxLength(32);
        invoice.Property(i => i.Payment).HasColumnName("payment").HasConversion<string>().HasMaxLength(32);

        invoice.Property(i => i.ScanBlobPath).HasColumnName("scan_blob_path");
        invoice.Property(i => i.ScanFileName).HasColumnName("scan_file_name");
        invoice.Property(i => i.ScanMimeType).HasColumnName("scan_mime_type");
        invoice.Property(i => i.ScanSize).HasColumnName("scan_size");

        // Метки «распознано, не подтверждено» — массивом текста: это множество ключей полей, и
        // отдельная таблица на него завела бы вторую сущность там, где нет ни одной своей колонки
        // кроме ключа. Postgres это умеет, а модуль от Postgres и так неотделим (своя схема).
        invoice.Property(i => i.Unconfirmed).HasColumnName("unconfirmed");

        invoice.Property(i => i.Data).HasColumnName("data").HasColumnType("jsonb");
        invoice.Property(i => i.CreatedBy).HasColumnName("created_by");
        invoice.Property(i => i.CreatedAt).HasColumnName("created_at");
        invoice.Property(i => i.UpdatedAt).HasColumnName("updated_at");

        // Дубликат «поставщик + номер + дата» (ТЗ COST-6.2) — ОГОВОРКА, а не запрет, поэтому индекс
        // не уникальный: он нужен затем, чтобы оговорку было чем искать на каждом сохранении.
        // Уникальный индекс запретил бы то, что ТЗ разрешает: у поставщика бывает два счёта с одним
        // номером в один день, и человек об этом знает больше нас.
        invoice.HasIndex(i => new { i.SupplierId, i.Number, i.IssuedOn }).HasDatabaseName("ix_invoices_duplicate");

        // Реестр счетов отбирается по сроку и состоянию оплаты (ТЗ COST-9.1: отбор «без срока»,
        // «просрочен»), и отбирается он на каждом открытии экрана.
        invoice.HasIndex(i => new { i.Payment, i.DueDate }).HasDatabaseName("ix_invoices_due");
    }

    /// <summary>
    /// Настройка контекста — одна на всех, кто его создаёт: и приложение
    /// (<c>CostsModule.RegisterServices</c>), и <c>dotnet ef</c> через
    /// <see cref="CostsDbContextFactory" />.
    ///
    /// <para>⚠️ История миграций названа ЯВНО, вместе со схемой. Без этого EF кладёт её в схему по
    /// умолчанию СВОЕЙ служебной модели, а не нашей, — то есть в <c>public</c>, в одну таблицу с
    /// историей ядра. Выглядело бы это исправной работой: миграции применяются, приложение
    /// поднимается. А затем обновление ядра встретило бы в своей истории строки, которых нет в его
    /// сборке, — и остановилось бы у заказчика, назвав чужую миграцию. Сторож на это есть: история
    /// модуля обязана лежать в схеме модуля, а в истории ядра его миграций быть не должно.</para>
    ///
    /// <para>Одна на всех потому, что разойтись эти две настройки могут только молча: миграции
    /// генерировались бы в одну схему, а применялись в другую.</para>
    /// </summary>
    public static void Configure(DbContextOptionsBuilder options, string? connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsHistoryTable(HistoryTableName, SchemaName));

    /// <summary>Имя таблицы истории миграций — то же, что у EF по умолчанию; своё только место.</summary>
    public const string HistoryTableName = "__EFMigrationsHistory";
}
