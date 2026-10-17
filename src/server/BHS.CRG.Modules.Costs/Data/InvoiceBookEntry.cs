using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Вид записи книги проводок счёта (E2, issue #1100).</summary>
public enum InvoiceBookKind
{
    /// <summary>Деньги счёта, как они лежали до первой записи в книгу, — каждое зерно своим днём.</summary>
    Opening = 0,

    /// <summary>Исправление счёта, требующего исправления: с причиной и номером версии.</summary>
    Correction = 1,

    /// <summary>Новая отметка оплаты неоплаченного счёта с книгой.</summary>
    Payment = 2,

    /// <summary>Отмена оплаты счёта с книгой или запертого.</summary>
    Unpayment = 3,

    /// <summary>Деньги счёта сдвинула смена допуска — без правки самого счёта.</summary>
    Alignment = 4,
}

/// <summary>
/// Заголовок записи книги проводок счёта (E2, issue #1100, spec v3.3 п. 1.1): кто, когда и почему
/// сдвинул деньги оплаченного счёта. Строки — <see cref="InvoiceBookLine" />, их может не быть вовсе.
///
/// <para><b>Книга только дописывается</b> (<see cref="IAppendOnly" />). Из неё строится взгляд «на
/// момент закрытия»: закрытый месяц показывает ровно то, что было в нём при закрытии, а исправление
/// ложится новой строкой днём исправления. Правленая строка книги сдвинула бы закрытый месяц так же
/// молча, как правка счёта до E2, — ради этого книга и заведена.</para>
///
/// <para>⚠️ <b>Пишет её одно место</b> — <see cref="InvoiceBook" />: оно берёт блокировку строки счёта,
/// сверяет книгу с основной таблицей и двигает номер записи у счёта. Перепись обращений стережёт
/// <c>InvoiceBookInventoryTests</c>.</para>
/// </summary>
public sealed class InvoiceBookEntry : IAppendOnly
{
    /// <summary>Для EF.</summary>
    private InvoiceBookEntry() { }

    public Guid Id { get; private set; }

    public Guid InvoiceId { get; private set; }

    /// <summary>
    /// Номер записи в книге счёта: 1, 2, … Уникален в счёте — две записи от одного «до» (гонка мимо
    /// блокировки строки) упрутся в ключ, а не дадут книге два продолжения.
    /// </summary>
    public int Seq { get; private set; }

    public InvoiceBookKind Kind { get; private set; }

    /// <summary>
    /// День записи D (spec п. 1.2): по нему «когда исправлено» и отметка предпросмотра. Строки несут
    /// свои дни — «своим днём» или днём D.
    /// </summary>
    public DateOnly Day { get; private set; }

    /// <summary>
    /// Допуск, по которому посчитаны деньги записи. По нему следующая запись узнаёт, сдвинула ли
    /// смена допуска деньги счёта мимо книги (страховка, spec п. 1.3 шаг 2).
    /// </summary>
    public decimal Tolerance { get; private set; }

    /// <summary>Причина: у исправления — от человека, у смены допуска — «допуск X → Y».</summary>
    public string? Reason { get; private set; }

    /// <summary>Номер версии, которую записало исправление; у прочих видов — <c>null</c>.</summary>
    public int? Version { get; private set; }

    public Guid? AuthorId { get; private set; }

    /// <summary>
    /// Имя автора НА МОМЕНТ ЗАПИСИ — как в журнале действий: книга — история, и переименование человека
    /// её не переписывает. Читать имя по <see cref="AuthorId" /> модулю нечем, а запись, сделанная до
    /// этого поля (PR 6b-2 E2), имени не знает — история показывает её прочерком. У служебных записей
    /// (открытие, страховочное выравнивание внутри чужой записи) имени нет нарочно: их пишет не человек.
    /// </summary>
    public string? AuthorName { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static InvoiceBookEntry Create(
        Guid invoiceId, int seq, BookRecord record, Guid? author, string? authorName, string? reason, int? version) => new()
    {
        Id = Guid.CreateVersion7(),
        InvoiceId = invoiceId,
        Seq = seq,
        Kind = record.Kind,
        Day = record.Day,
        Tolerance = record.Tolerance,
        Reason = reason,
        Version = version,
        AuthorId = author,
        AuthorName = authorName,
        At = DateTimeOffset.UtcNow,
    };

    internal static void Map(ModelBuilder builder)
    {
        var entry = builder.Entity<InvoiceBookEntry>();
        entry.ToTable("invoice_book_entries");
        entry.HasKey(e => e.Id);

        entry.Property(e => e.Id).HasColumnName("id");
        entry.Property(e => e.InvoiceId).HasColumnName("invoice_id");
        entry.Property(e => e.Seq).HasColumnName("seq");
        entry.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        entry.Property(e => e.Day).HasColumnName("day");
        // Точность — как у допуска в настройке: число, которым посчитаны деньги, обязано вернуться тем же.
        entry.Property(e => e.Tolerance).HasColumnName("tolerance").HasPrecision(18, 2);
        entry.Property(e => e.Reason).HasColumnName("reason");
        entry.Property(e => e.Version).HasColumnName("version");
        entry.Property(e => e.AuthorId).HasColumnName("author_id");
        entry.Property(e => e.AuthorName).HasColumnName("author_name");
        entry.Property(e => e.At).HasColumnName("at");

        // Две записи с одним номером — два продолжения одной книги от одного «до»: отказ ключом.
        entry.HasIndex(e => new { e.InvoiceId, e.Seq }).IsUnique().HasDatabaseName("ux_invoice_book_entries_seq");

        // ⚠️ RESTRICT, а не каскад: счёт с книгой не удаляется (spec п. 1.5), и каскад снёс бы историю
        // молча — мимо отказа дописываемого набора, который видит только отслеживаемые строки.
        entry.HasOne<Invoice>().WithMany().HasForeignKey(e => e.InvoiceId).OnDelete(DeleteBehavior.Restrict);

        var line = builder.Entity<InvoiceBookLine>();
        line.ToTable("invoice_book_lines");
        line.HasKey(l => l.Id);

        line.Property(l => l.Id).HasColumnName("id");
        line.Property(l => l.EntryId).HasColumnName("entry_id");
        line.Property(l => l.Ordinal).HasColumnName("ordinal");
        line.Property(l => l.ConstructionId).HasColumnName("construction_id");
        line.Property(l => l.SectionId).HasColumnName("section_id");
        line.Property(l => l.ArticleId).HasColumnName("article_id");
        line.Property(l => l.SupplierId).HasColumnName("supplier_id");
        line.Property(l => l.Day).HasColumnName("day");
        line.Property(l => l.Gross).HasColumnName("gross").HasPrecision(18, 2);
        line.Property(l => l.Net).HasColumnName("net").HasPrecision(18, 2);
        line.Property(l => l.VatUnknown).HasColumnName("vat_unknown");
        line.Property(l => l.Shifted).HasColumnName("shifted");
        line.Ignore(l => l.Cell);
        line.Ignore(l => l.Value);

        line.HasOne<InvoiceBookEntry>().WithMany().HasForeignKey(l => l.EntryId).OnDelete(DeleteBehavior.Restrict);
        line.HasIndex(l => new { l.EntryId, l.Ordinal }).IsUnique().HasDatabaseName("ux_invoice_book_lines_order");

        // Статья и стройка — не обе сразу, как у доли; у остатка — ни той ни другой.
        line.ToTable(t => t.HasCheckConstraint("ck_invoice_book_lines_one_target",
            "construction_id IS NULL OR article_id IS NULL"));
    }
}

/// <summary>
/// Денежная строка книги (spec п. 1.1): ячейка, день и деньги — с НДС и без. Ячейка — цель доли
/// (стройка с разделом или статья) и поставщик счёта; без цели — остаток счёта.
///
/// <para>⚠️ Стройка, раздел, статья и поставщик лежат идентификаторами и записи ядра НЕ держат
/// (<c>CostsReferences</c>): книга — история, и держи она стройку, удалить её не дала бы уже никакая
/// правка. Удалённый объект показывает чтение.</para>
/// </summary>
public sealed class InvoiceBookLine : IAppendOnly
{
    /// <summary>Для EF.</summary>
    private InvoiceBookLine() { }

    public Guid Id { get; private set; }

    public Guid EntryId { get; private set; }

    /// <summary>Порядок строк в записи — чтобы книгу читали всегда одинаково.</summary>
    public int Ordinal { get; private set; }

    public Guid? ConstructionId { get; private set; }

    public Guid? SectionId { get; private set; }

    public Guid? ArticleId { get; private set; }

    public Guid? SupplierId { get; private set; }

    public DateOnly Day { get; private set; }

    public decimal Gross { get; private set; }

    public decimal Net { get; private set; }

    /// <summary>
    /// «НДС неизвестен» — исторический признак: в числе есть деньги, посчитанные без ставки. В
    /// сравнениях книги с документом он не участвует (spec п. 1.3 шаг 4).
    /// </summary>
    public bool VatUnknown { get; private set; }

    /// <summary>
    /// Строка легла днём записи, а не своим днём, — корректировка закрытого периода (PR 6d E2,
    /// <see cref="Grain.Shifted" />).
    ///
    /// <para>⚠️ <b>Признак влияет не только на показ, но и на проводку</b> (ревью PR #1395): своя часть
    /// книги пары — строки без него, и по ней правило (б′) решает, ляжет ли следующая разница своим днём.</para>
    ///
    /// <para>⚠️ <b>У строк, записанных до PR 6d, признак восстановлен догадкой</b> миграцией
    /// <c>InvoiceBookShifted</c> (решение владельца 17.10.2026): открытие и оплата — нет; исправление,
    /// отмена оплаты и выравнивание — да, если день строки равен дню записи и раньше у счёта не было строки
    /// открытия или оплаты тем же днём (<c>InvoiceBookShifted.Guess</c>). Ошибается догадка на склеенной
    /// строке: прежде своя и перенесённая строки одной ячейки на день записи складывались в одну, и разделить
    /// их нечем. И копия, снятая до PR 6d, восстанавливается с признаком «нет» у всех своих строк:
    /// восстановление даёт колонке, которой в копии нет, значение по умолчанию. Ошибочное «нет» — прежнее
    /// поведение проводки, не хуже.</para>
    /// </summary>
    public bool Shifted { get; private set; }

    /// <summary>Ячейка строки — тем же значением, каким её считает проводка.</summary>
    public BookCell Cell => new(
        ConstructionId is null && ArticleId is null ? null : new AllocationTarget(ConstructionId, SectionId, ArticleId),
        SupplierId);

    /// <summary>Строка значением — тем, каким её посчитала проводка.</summary>
    public Grain Value => new(Cell, Day, Gross, Net, VatUnknown, Shifted);

    public static InvoiceBookLine Create(Guid entryId, int ordinal, Grain line) => new()
    {
        Id = Guid.CreateVersion7(),
        EntryId = entryId,
        Ordinal = ordinal,
        ConstructionId = line.Cell.Target?.ConstructionId,
        SectionId = line.Cell.Target?.SectionId,
        ArticleId = line.Cell.Target?.ArticleId,
        SupplierId = line.Cell.SupplierId,
        Day = line.Day,
        Gross = line.Gross,
        Net = line.Net,
        VatUnknown = line.VatUnknown,
        Shifted = line.Shifted,
    };
}
