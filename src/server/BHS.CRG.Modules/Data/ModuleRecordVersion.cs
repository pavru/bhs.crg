using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Modules.Data;

/// <summary>
/// Прежняя версия записи модуля (ТЗ CORE-36, change <c>plan-12-record-versions</c>): содержимое записи
/// таким, каким оно было до новой версии, с причиной, автором и временем.
///
/// <para><b>Действующая версия — в основной таблице записи</b>, прежние — в таблице наследника. Номер
/// действующей хранит сама запись (<c>CurrentVersion</c>). Иначе каждый запрос сумм помнил бы отбор
/// «действующая версия», и одна забытая проверка молча удвоила бы цифры: здесь запросу сумм отбирать
/// нечего — он просто не читает историю. Что не читает, стережёт перепись
/// <c>RecordVersionInventoryTests</c>.</para>
///
/// <para><b>Только дописывается</b> (<see cref="IAppendOnly" />): версия — свидетельство о том, что было,
/// и правленая версия свидетельствовала бы о другом. Правку через отслеживание отвергает
/// <see cref="ModuleDbContext" />, обход отслеживания — перепись.</para>
///
/// <para>Наследник называет свою таблицу и тип своей записи; колонки, единственный индекс
/// <c>(RecordId, Number)</c> и связь с записью даёт <see cref="Map{TVersion, TRecord}" />. Индекс — чтобы его
/// не забывали: два исправления с одним номером — гонка мимо блокировки строки записи, и отказать ей должен
/// ключ. Связь — всегда <c>Restrict</c>: каскад удалял бы историю базой, мимо отказа контекста, который
/// видит только отслеживаемые строки.</para>
///
/// <para>⚠️ Версии счёта (<c>InvoiceVersion</c>) на этот тип не переведены: перенос — миграция живых
/// данных ради имени. Исключение названо в переписи с причиной.</para>
/// </summary>
public abstract class ModuleRecordVersion : IAppendOnly
{
    /// <summary>Для EF.</summary>
    protected ModuleRecordVersion() { }

    protected ModuleRecordVersion(Guid recordId, int number, string content, string reason, Guid? authorId)
    {
        Id = Guid.CreateVersion7();
        RecordId = recordId;
        Number = number;
        Content = content;
        Reason = reason;
        AuthorId = authorId;
        At = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Запись, чья это версия.</summary>
    public Guid RecordId { get; private set; }

    /// <summary>Номер версии в записи: 1, 2, … Уникален в записи; что он значит — решает наследник.</summary>
    public int Number { get; private set; }

    /// <summary>
    /// Содержимое записи документом JSON. Версия читается только целиком, отбор по её полям не нужен —
    /// поэтому один документ, а не строки в своих таблицах.
    /// </summary>
    public string Content { get; private set; } = "{}";

    /// <summary>
    /// Причина новой версии. Может быть пустой: не каждую версию рождает исправление — у предложения её
    /// рождает «отправлено». Обязательна ли она, решает тот, кто версию пишет.
    /// </summary>
    public string Reason { get; private set; } = string.Empty;

    public Guid? AuthorId { get; private set; }

    public DateTimeOffset At { get; private set; }

    /// <summary>
    /// Таблица версий: колонки, единственный индекс номера в записи и связь с записью <c>Restrict</c> —
    /// запись с историей не удаляется ни каскадом, ни молча. Удалить запись с версиями модуль обязан
    /// отказом по смыслу раньше базы (у накладной — по <c>CurrentVersion</c>).
    /// </summary>
    public static EntityTypeBuilder<TVersion> Map<TVersion, TRecord>(ModelBuilder builder, string table)
        where TVersion : ModuleRecordVersion
        where TRecord : class
    {
        var version = builder.Entity<TVersion>();
        version.ToTable(table);
        version.HasKey(v => v.Id);

        version.Property(v => v.Id).HasColumnName("id");
        version.Property(v => v.RecordId).HasColumnName("record_id");
        version.Property(v => v.Number).HasColumnName("number");
        version.Property(v => v.Content).HasColumnName("content").HasColumnType("jsonb");
        version.Property(v => v.Reason).HasColumnName("reason");
        version.Property(v => v.AuthorId).HasColumnName("author_id");
        version.Property(v => v.At).HasColumnName("at");

        version.HasIndex(v => new { v.RecordId, v.Number }).IsUnique().HasDatabaseName($"ux_{table}_number");
        version.HasOne<TRecord>().WithMany().HasForeignKey(v => v.RecordId).OnDelete(DeleteBehavior.Restrict);
        return version;
    }
}
