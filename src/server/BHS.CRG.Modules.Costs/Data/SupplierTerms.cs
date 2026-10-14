using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Условия поставщика — отсрочка платежа в днях (ТЗ COST-9.2; задача C4, issue #1080). Подставляется в
/// счёт, когда у него выбран поставщик, и от неё правило считает срок «оплатить до».
///
/// <para><b>Данные модуля, а не поле организации.</b> Организация — общий справочник ядра: её видят и
/// исполнительная документация, и сотрудники без права на счета, а условия оплаты касаются только
/// счетов. Лимитов здесь нет и не будет (ТЗ COST-Q9).</para>
///
/// <para><b>Ключ — запись поставщика</b>, как у соответствий наименований (<see cref="SupplierMatch" />):
/// одна строка на поставщика, и «две отсрочки у одного поставщика» не бывает по устройству таблицы.</para>
///
/// <para>⚠️ <b>Условия подставляются, а не читаются на лету.</b> Отсрочка ложится в счёт значением, и
/// дальше счёт живёт сам: смена условий уже заведённые счета не трогает — у них отсрочка из бумаги или
/// из условий на тот день, и бухгалтер о сроке уже знает. Так же устроено правило срока (решение
/// владельца 14.10.2026).</para>
/// </summary>
public sealed class SupplierTerms
{
    /// <summary>Для EF.</summary>
    private SupplierTerms() { }

    /// <summary>Запись организации — поставщика. Без внешнего ключа, как у счёта: справочник в ядре.</summary>
    public Guid SupplierId { get; private set; }

    /// <summary>Отсрочка платежа в днях — те же пределы, что у поля счёта (<see cref="CostsValues" />).</summary>
    public int DeferralDays { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Кто записал условия последним. Помнит, а не держит: справка для разбора.</summary>
    public Guid? UpdatedBy { get; private set; }

    /// <summary>Имя записавшего — снимком, по той же причине, что у соответствия.</summary>
    public string? UpdatedByName { get; private set; }

    public static SupplierTerms Create(Guid supplierId, int deferralDays, Guid? by, string? byName)
    {
        var terms = new SupplierTerms { SupplierId = supplierId };
        terms.Set(deferralDays, by, byName);
        return terms;
    }

    public void Set(int deferralDays, Guid? by, string? byName)
    {
        DeferralDays = deferralDays;
        UpdatedBy = by;
        UpdatedByName = byName is { Length: > SupplierMatch.ByNameLength } ? byName[..SupplierMatch.ByNameLength] : byName;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Отсрочка, которую счёт получает от условий поставщика при этой записи; <c>null</c> — подставлять
    /// нечего, отсрочка остаётся как пришла.
    ///
    /// <para>Подставляется, когда поставщик в этой записи СМЕНИЛСЯ (новый счёт — смена с пустого), у
    /// нового поставщика условия есть, а отсрочку той же записью не правили. И только поверх пустой
    /// отсрочки либо отсрочки из условий прежнего поставщика: вписанное человеком или прочитанное из
    /// бумаги условиями не перекрывается.</para>
    ///
    /// <para>У нового поставщика условий нет — отсрочка остаётся, даже взятая у прежнего: стереть её
    /// значило бы молча снять и срок, а неверное значение человек видит в поле и правит.</para>
    /// </summary>
    /// <param name="was">Поставщик и отсрочка счёта до записи; у нового счёта — пустые.</param>
    /// <param name="wasTerms">Отсрочка в условиях прежнего поставщика.</param>
    /// <param name="terms">Отсрочка в условиях нового поставщика.</param>
    public static int? Substituted(InvoiceColumns columns, (Guid? SupplierId, int? DeferralDays) was, int? wasTerms, int? terms) =>
        columns.SupplierId is not null
        && columns.SupplierId != was.SupplierId
        && terms is { } days
        && columns.DeferralDays == was.DeferralDays
        && (was.DeferralDays is null || was.DeferralDays == wasTerms)
        && columns.DeferralDays != days
            ? days
            : null;

    internal static void Map(ModelBuilder builder)
    {
        var terms = builder.Entity<SupplierTerms>();
        terms.ToTable("supplier_terms");
        terms.HasKey(t => t.SupplierId);

        terms.Property(t => t.SupplierId).HasColumnName("supplier_id").ValueGeneratedNever();
        terms.Property(t => t.DeferralDays).HasColumnName("deferral_days");
        terms.Property(t => t.UpdatedAt).HasColumnName("updated_at");
        terms.Property(t => t.UpdatedBy).HasColumnName("updated_by");
        terms.Property(t => t.UpdatedByName).HasColumnName("updated_by_name").HasMaxLength(SupplierMatch.ByNameLength);

        // Версия строки — системная колонка xmin, как у соответствия: окно открыто долго, и правка
        // поверх чужой правки затёрла бы её молча.
        terms.Property<uint>(CostsDbContext.RowVersion).IsRowVersion();
    }
}
