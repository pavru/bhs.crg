using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Кто записал счёт последним (COST-6.4, решение владельца Э4 07.11.2026, design D4 вариант А) — снимок в
/// строке счёта. Нужен индикатору состояния для пункта «изменили, пока вы правили»: «Изменил Петров · 12:04»
/// или «Дописало распознавание · 12:04». Время уже есть (<see cref="Invoice.UpdatedAt" />), здесь — автор.
/// </summary>
public static class InvoiceAuthor
{
    /// <summary>Записал человек в запросе.</summary>
    public const string User = "user";

    /// <summary>Записало фоновое распознавание скана (<c>InvoiceDesk.MergeAsync</c>).</summary>
    public const string Recognition = "recognition";

    /// <summary>Записал сам экземпляр — фоновая запись без человека.</summary>
    public const string System = "system";

    /// <summary>Предел снимка имени — тот же, что у соответствий наименований.</summary>
    public const int NameLength = SupplierMatch.ByNameLength;

    /// <summary>
    /// Поставить автора каждому счёту, который это сохранение реально меняет, — одним местом, при
    /// сохранении контекста, а не у каждого адреса: адресов, пишущих счёт, больше десяти, и одиннадцатый
    /// забыл бы.
    ///
    /// <para>⚠️ <b>Только изменённым счетам.</b> Запись, которая ничего не меняет (<c>POST …/draft</c> у
    /// черновика), не трогает ни версию, ни автора: иначе пустая запись давала бы соседу ложное
    /// «изменили». Изменённым считается счёт, у которого изменено что-то, кроме самих колонок автора.</para>
    /// </summary>
    /// <param name="via">Род записи, названный связкой (<see cref="Recognition" />); <c>null</c> — по
    /// тому, кто действует: человек — <see cref="User" />, никто — <see cref="System" />.</param>
    /// <remarks>Изменения находит сам <see cref="ChangeTracker.Entries{TEntity}" /> (он зовёт
    /// <c>DetectChanges</c>), а поставленного здесь автора — сохранение следом: отдельный проход не нужен.</remarks>
    internal static void Stamp(ChangeTracker tracker, IModuleUser? user, string? via)
    {
        foreach (var entry in tracker.Entries<Invoice>())
        {
            if (!Written(entry)) continue;

            if (via is not null) entry.Entity.WrittenBy(null, null, via);
            else if (user?.Id is { } id) entry.Entity.WrittenBy(id, user.Name, User);
            else entry.Entity.WrittenBy(null, null, System);
        }
    }

    private static readonly HashSet<string> Own =
        [nameof(Invoice.UpdatedBy), nameof(Invoice.UpdatedByName), nameof(Invoice.UpdatedVia)];

    private static bool Written(EntityEntry<Invoice> entry) => entry.State switch
    {
        EntityState.Added => true,
        EntityState.Modified => entry.Properties.Any(p => p.IsModified && !Own.Contains(p.Metadata.Name)),
        _ => false,
    };

    internal static void Map(ModelBuilder builder)
    {
        // Колонки допускают NULL и умолчаний не имеют: NULL — «неизвестно» (счёт записан до них или
        // восстановлен из копии старше них), а не «никто».
        var invoice = builder.Entity<Invoice>();
        invoice.Property(i => i.UpdatedBy).HasColumnName("updated_by");
        invoice.Property(i => i.UpdatedByName).HasColumnName("updated_by_name").HasMaxLength(NameLength);
        invoice.Property(i => i.UpdatedVia).HasColumnName("updated_via").HasMaxLength(16);
    }
}

public sealed partial class Invoice
{
    /// <summary>Кто записал последним — учётная запись; только у рода <see cref="InvoiceAuthor.User" />.</summary>
    public Guid? UpdatedBy { get; private set; }

    /// <summary>
    /// Имя записавшего — снимком на момент записи, как у соответствий наименований: учётную запись
    /// могут переименовать или удалить, а вопрос «кто изменил» останется.
    /// </summary>
    public string? UpdatedByName { get; private set; }

    /// <summary>Род последней записи (<see cref="InvoiceAuthor" />); <c>null</c> — неизвестно.</summary>
    public string? UpdatedVia { get; private set; }

    internal void WrittenBy(Guid? by, string? name, string via)
    {
        UpdatedBy = by;
        UpdatedByName = SupplierMatch.NameSnapshot(name);
        UpdatedVia = via;
    }
}
