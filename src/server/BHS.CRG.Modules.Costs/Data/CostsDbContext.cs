using BHS.CRG.Domain.Common;
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
    /// Строки счёта (C2, issue #1078). Своим набором, а не полем счёта: на строку опирается код —
    /// отбор «ждёт номенклатуры», сверка сумм, разноска по количеству (F1).
    /// </summary>
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();

    public DbSet<InvoiceAllocation> InvoiceAllocations => Set<InvoiceAllocation>();

    /// <summary>
    /// Раскладка таблицы счёта. Вручную, а не соглашениями EF: имена таблиц и колонок здесь — это
    /// то, что увидит человек в отчёте резервной копии и в запросе к базе, а соглашение по умолчанию
    /// дало бы «Invoices» и «IssuedOn» в схеме, где всё остальное названо по-русски и через
    /// подчёркивание.
    /// </summary>
    /// <summary>Имя теневого свойства с версией строки счёта.</summary>
    private const string RowVersion = "RowVersion";

    /// <summary>
    /// Запись, проигравшая одновременной, отвечает отказом 409, а не 500.
    ///
    /// <para>Переводится ЗДЕСЬ, одним местом, а не у каждого адреса: адресов, пишущих счёт, восемь, и
    /// девятый забыл бы. Исключение EF наружу — это «внутренняя ошибка сервера» о ситуации, которая
    /// ошибкой сервера не является: человеку надо повторить, а не звать администратора.</para>
    ///
    /// <para>Сюда же попадает удаление строки, которую одновременный запрос уже удалил: EF ждёт одну
    /// затронутую запись и получает ноль. Это та же гонка, и ответ тот же.</para>
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw Concurrent(e);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw Concurrent(e);
        }
    }

    private static ConflictException Concurrent(DbUpdateConcurrencyException e) =>
        new("Счёт изменили одновременно с этой правкой, и она не записана — целиком, ни одной частью. " +
            "Перечитайте счёт и повторите: записанная поверх, она затёрла бы чужую правку или удвоила бы свою.",
            e);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var invoice = builder.Entity<Invoice>();
        invoice.ToTable("invoices");
        invoice.HasKey(i => i.Id);

        invoice.Property(i => i.Id).HasColumnName("id");
        invoice.Property(i => i.DocumentTypeId).HasColumnName("document_type_id");
        invoice.Property(i => i.Number).HasColumnName("number").HasMaxLength(Invoice.NumberLength);
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

        invoice.Property(i => i.PaidOn).HasColumnName("paid_on");
        invoice.Property(i => i.PaymentDocument).HasColumnName("payment_document");
        invoice.Property(i => i.PaidAt).HasColumnName("paid_at");
        invoice.Property(i => i.PaidBy).HasColumnName("paid_by");
        invoice.Property(i => i.RemainderAccountingOn).HasColumnName("remainder_accounting_on");

        // «Оплачен» и дата платежа — одно утверждение в двух колонках, и держит его база: оплаченный
        // счёт без даты не попал бы ни в один период, а дата у неоплаченного — попала бы в затраты.
        invoice.ToTable(t => t.HasCheckConstraint("ck_invoices_paid_on",
            "(payment = 'Paid') = (paid_on IS NOT NULL) AND (payment = 'Paid' OR remainder_accounting_on IS NULL)"));

        // Реестр отбирает период по дате платежа (ТЗ COST-20.1). Частичный: неоплаченных большинство.
        invoice.HasIndex(i => i.PaidOn).HasDatabaseName("ix_invoices_paid_on").HasFilter("paid_on IS NOT NULL");

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

        // Версия строки счёта — токен конкурентности (issue #1173). Счёт — корень: строки и разноска —
        // его части, и каждая их правка отмечается у него самого (Invoice.ContentChanged). Поэтому из
        // двух одновременных правок одного счёта вторая отказывает при записи целиком — а не удваивает
        // строки и не записывает «разобран» поверх строки, у которой только что сняли позицию.
        //
        // ⚠️ Это системная колонка PostgreSQL (xmin), а не своя: её меняет ЛЮБАЯ запись строки, и
        // забыть её поднять нельзя. Колонки в таблице не прибавляется — миграция только сообщает
        // модели, что свойство есть.
        //
        // ⚠️ Форма версию НЕ присылает, и окно конфликта — время одного запроса, а не время, пока
        // счёт открыт. Защита от устаревшей формы — другое решение (как ifMatch у наборов данных): у
        // формы появился бы отказ, которого сейчас нет.
        invoice.Property<uint>(RowVersion).IsRowVersion();

        // Дубликат «поставщик + номер + дата» (ТЗ COST-6.2) — ОГОВОРКА, а не запрет, поэтому индекс
        // не уникальный: он нужен затем, чтобы оговорку было чем искать на каждом сохранении.
        // Уникальный индекс запретил бы то, что ТЗ разрешает: у поставщика бывает два счёта с одним
        // номером в один день, и человек об этом знает больше нас.
        invoice.HasIndex(i => new { i.SupplierId, i.Number, i.IssuedOn }).HasDatabaseName("ix_invoices_duplicate");

        // Реестр счетов отбирается по сроку и состоянию оплаты (ТЗ COST-9.1: отбор «без срока»,
        // «просрочен»), и отбирается он на каждом открытии экрана.
        invoice.HasIndex(i => new { i.Payment, i.DueDate }).HasDatabaseName("ix_invoices_due");

        // Удаление записи ядра спрашивает, кто её держит (ТЗ CORE-34.1, issue #1094), — и спрашивает по
        // этой колонке у каждой записи справочника. Частичный: пустых значений вопрос не касается.
        // Поставщика тот же вопрос находит по ix_invoices_duplicate — он там первый.
        invoice.HasIndex(i => i.PayerId).HasDatabaseName("ix_invoices_payer").HasFilter("payer_id IS NOT NULL");

        MapLines(builder);
        MapAllocations(builder);
    }

    /// <summary>
    /// Раскладка строк счёта (C2, issue #1078).
    ///
    /// <para>⚠️ Внешний ключ ЕСТЬ — в отличие от ссылок на ядро. Разница принципиальная: строка и счёт
    /// лежат в ОДНОЙ схеме и едут одним набором миграций, то есть ключ здесь ничего не связывает между
    /// модулем и ядром. А без него удаление счёта оставляло бы строки сиротами, и находились бы они
    /// только запросом в базу.</para>
    /// </summary>
    private static void MapLines(ModelBuilder builder)
    {
        var line = builder.Entity<InvoiceLine>();
        line.ToTable("invoice_lines");
        line.HasKey(l => l.Id);

        line.Property(l => l.Id).HasColumnName("id");
        line.Property(l => l.InvoiceId).HasColumnName("invoice_id");
        line.Property(l => l.Ordinal).HasColumnName("ordinal");
        line.Property(l => l.NomenclatureId).HasColumnName("nomenclature_id");
        line.Property(l => l.SupplierText).HasColumnName("supplier_text");
        line.Property(l => l.SupplierCode).HasColumnName("supplier_code").HasMaxLength(InvoiceLine.SupplierCodeLength);
        line.Property(l => l.Unit).HasColumnName("unit").HasMaxLength(InvoiceLine.UnitLength);

        // Количество — три знака после запятой: кабель мерят метрами с сантиметрами, и «7,25 м» в
        // бумаге обязано остаться «7,25 м». Деньги — две, как у счёта, и по той же причине (не double).
        line.Property(l => l.Quantity).HasColumnName("quantity").HasPrecision(18, 3);
        line.Property(l => l.Price).HasColumnName("price").HasPrecision(18, 2);
        line.Property(l => l.VatRate).HasColumnName("vat_rate").HasPrecision(5, 2);
        line.Property(l => l.VatAmount).HasColumnName("vat_amount").HasPrecision(18, 2);
        line.Property(l => l.Amount).HasColumnName("amount").HasPrecision(18, 2);

        line.Property(l => l.Note).HasColumnName("note");
        line.Property(l => l.CreatedAt).HasColumnName("created_at");
        line.Property(l => l.UpdatedAt).HasColumnName("updated_at");

        line.HasOne<Invoice>()
            .WithMany()
            .HasForeignKey(l => l.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Строки читаются ТОЛЬКО счётом и всегда в порядке бумаги — это и есть индекс.
        line.HasIndex(l => new { l.InvoiceId, l.Ordinal }).HasDatabaseName("ix_invoice_lines_order");

        // Отбор «Разобрать» (ТЗ COST-6.2) ищет строки БЕЗ позиции номенклатуры, и ищет на каждом
        // открытии реестра. Индекс частичный: строк с позицией со временем станет подавляющее
        // большинство, и держать их в этом индексе незачем.
        line.HasIndex(l => l.InvoiceId)
            .HasDatabaseName("ix_invoice_lines_unmatched")
            .HasFilter("nomenclature_id IS NULL");

        // Удаление записи ядра спрашивает, кто её держит (ТЗ CORE-34.1, issue #1094), — и спрашивает по
        // этой колонке у каждой записи справочника. Частичный: пустых значений вопрос не касается.
        line.HasIndex(l => l.NomenclatureId)
            .HasDatabaseName("ix_invoice_lines_nomenclature")
            .HasFilter("nomenclature_id IS NOT NULL");
    }

    /// <summary>
    /// Части разноски строк (задача F1, issue #1085, ТЗ COST-10) и счёта без строк (F2, issue #1086). Хранится введённое человеком —
    /// количество или сумма, — посчитанное не хранится (см. <see cref="InvoiceAllocation" />).
    /// </summary>
    private static void MapAllocations(ModelBuilder builder)
    {
        var part = builder.Entity<InvoiceAllocation>();
        part.ToTable("invoice_allocations");
        part.HasKey(a => a.Id);

        part.Property(a => a.Id).HasColumnName("id");
        part.Property(a => a.InvoiceId).HasColumnName("invoice_id");
        part.Property(a => a.LineId).HasColumnName("line_id");
        part.Property(a => a.Ordinal).HasColumnName("ordinal");
        part.Property(a => a.ConstructionId).HasColumnName("construction_id");
        part.Property(a => a.SectionId).HasColumnName("section_id");
        part.Property(a => a.ArticleId).HasColumnName("article_id");
        part.Ignore(a => a.Target);

        // Те же точности, что у строки: часть количества не бывает точнее самой строки, а часть суммы —
        // точнее копейки. Пусти мы точнее, база округлила бы молча, и баланс, сошедшийся при сохранении,
        // разошёлся бы на первом чтении. Поэтому лишние знаки отвергает разбор, а не режет база.
        part.Property(a => a.Quantity).HasColumnName("quantity").HasPrecision(18, 3);
        part.Property(a => a.Amount).HasColumnName("amount").HasPrecision(18, 2);

        part.Property(a => a.AccountingOn).HasColumnName("accounting_on");
        part.Property(a => a.CreatedAt).HasColumnName("created_at");
        part.Property(a => a.UpdatedAt).HasColumnName("updated_at");

        // Затраты периода отбирают доли по учётной дате (ТЗ COST-16, COST-20). Частичный: у неоплаченных
        // счетов даты нет.
        part.HasIndex(a => a.AccountingOn)
            .HasDatabaseName("ix_invoice_allocations_accounting").HasFilter("accounting_on IS NOT NULL");

        // Удалили строку — уходят и её части: разноска строки без строки — это деньги ниоткуда. Строки
        // при правке набора правятся на месте (C2), так что каскад срабатывает только на настоящем
        // удалении строки.
        part.HasOne<InvoiceLine>()
            .WithMany()
            .HasForeignKey(a => a.LineId)
            .OnDelete(DeleteBehavior.Cascade);

        part.HasOne<Invoice>()
            .WithMany()
            .HasForeignKey(a => a.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Разноску читают счётом целиком и в порядке частей строки.
        part.HasIndex(a => new { a.InvoiceId, a.LineId, a.Ordinal }).HasDatabaseName("ix_invoice_allocations_order");

        // Удаление записи ядра спрашивает, кто её держит (ТЗ CORE-34.1, issue #1094), — и спрашивает по
        // этой колонке у каждой записи справочника. Частичный: пустых значений вопрос не касается.
        // Отбор частей по стройке для затрат (G5) — отдельный вопрос со своим индексом, если этого
        // ему не хватит.
        part.HasIndex(a => a.ConstructionId)
            .HasDatabaseName("ix_invoice_allocations_construction").HasFilter("construction_id IS NOT NULL");
        part.HasIndex(a => a.SectionId)
            .HasDatabaseName("ix_invoice_allocations_section").HasFilter("section_id IS NOT NULL");
        part.HasIndex(a => a.ArticleId)
            .HasDatabaseName("ix_invoice_allocations_article").HasFilter("article_id IS NOT NULL");

        // Часть счёта целиком (без строки, F2) — только суммой: делить количество не из чего. Ограничением
        // базы, а не одной проверкой разбора: часть счёта с метрами разнесла бы ничто, и узнали бы об этом
        // по отчёту, где у стройки не хватает денег.
        part.ToTable(t => t.HasCheckConstraint("ck_invoice_allocations_document_amount",
            "line_id IS NOT NULL OR (quantity IS NULL AND amount IS NOT NULL)"));

        // Цель части — стройка ИЛИ статья вне строек, ровно одно (F3, issue #1087, ТЗ COST-10.1); раздел —
        // только внутри стройки. Ограничением базы по той же причине: часть с двумя целями легла бы в
        // затраты дважды, без цели — никуда, и отчёт разошёлся бы со счётом молча.
        part.ToTable(t => t.HasCheckConstraint("ck_invoice_allocations_one_target",
            "(construction_id IS NULL) <> (article_id IS NULL) AND (section_id IS NULL OR construction_id IS NOT NULL)"));
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
