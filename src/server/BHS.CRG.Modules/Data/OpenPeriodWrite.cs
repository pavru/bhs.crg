using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Data;

/// <summary>
/// Запись с учётной датой — так, чтобы период не закрыли посреди неё (ТЗ CORE-35; задача E1a,
/// issue #1081).
///
/// <para><b>От чего это.</b> Путь записи проверяет «период открыт» и сохраняет. Между двумя шагами
/// период могут закрыть — и запись ляжет в закрытый месяц, не нарушив ни одной проверки: обе
/// стороны были правы на момент своего чтения. Закрытие периода поэтому берёт совещательный замок
/// PostgreSQL исключительным, а пишущий держит его разделяемым до конца транзакции: закрытие ждёт
/// начатые записи, новые записи ждут закрытие.</para>
///
/// <para><b>Почему связкой, а не вызовом «возьми замок».</b> Порядок здесь — всё: транзакция → замок
/// → снимок границ → запись → фиксация. Снимок, прочитанный до замка, устарел бы, пока замок ждали;
/// замок, взятый вне транзакции, снялся бы тем же запросом и не защитил бы ничего — молча. Порядок
/// задаёт этот метод, а не память автора очередного пути записи.</para>
///
/// <para>Замок берётся на соединении МОДУЛЯ, а снимок читается через порт — соединением ядра. Взять
/// замок на соединении ядра было бы нельзя: два соединения одного запроса, ждущие друг друга, —
/// взаимная блокировка, которую детектор PostgreSQL не видит.</para>
/// </summary>
public static class OpenPeriodWrite
{
    /// <summary>
    /// Ключ замка. ⚠️ Повторяет <c>AdvisoryLockKeys.PeriodWrite</c> ядра: сослаться туда контрактам
    /// нельзя. Разойдясь, две константы дали бы два разных замка — запись и закрытие перестали бы
    /// друг друга видеть, и не упало бы ничего. Сверяет их сторож <c>PeriodClosureInventoryTests</c>.
    /// </summary>
    public const long LockKey = 1081_2026;

    /// <summary>
    /// Выполнить запись при неподвижных границах периода. Транзакцию открывает сам, если её нет;
    /// открытую — использует, и тогда замок живёт до ЕЁ конца.
    /// </summary>
    /// <param name="write">
    /// Запись. Получает границы, по которым решает, открыт ли период её учётной даты, и сохраняет
    /// через тот же контекст. Отказ — исключением: транзакция откатится.
    /// </param>
    public static async Task<T> InOpenPeriodAsync<T>(
        this ModuleDbContext db, IModulePeriods periods, Func<PeriodBoundaries, Task<T>> write,
        CancellationToken ct = default)
    {
        var own = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            await HoldAsync(db, ct);
            var result = await write(await periods.BoundariesAsync(ct));
            if (own is not null) await own.CommitAsync(ct);
            return result;
        }
        finally
        {
            if (own is not null) await own.DisposeAsync();
        }
    }

    /// <inheritdoc cref="InOpenPeriodAsync{T}" />
    public static Task InOpenPeriodAsync(
        this ModuleDbContext db, IModulePeriods periods, Func<PeriodBoundaries, Task> write,
        CancellationToken ct = default) =>
        db.InOpenPeriodAsync(periods, async boundaries =>
        {
            await write(boundaries);
            return true;
        }, ct);

    /// <summary>
    /// Взять разделяемый замок до конца текущей транзакции. ⚠️ Вне транзакции — ОТКАЗ: в режиме
    /// автофиксации замок снялся бы в том же запросе, и вызов выглядел бы защитой, не будучи ею.
    /// </summary>
    public static async Task HoldAsync(ModuleDbContext db, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Замок записи в учёт берётся только внутри транзакции: вне её он снимается тем же " +
                "запросом и не защищает ничего. Пишите через InOpenPeriodAsync — он открывает " +
                "транзакцию сам.");

        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock_shared({LockKey})", ct);
    }
}
