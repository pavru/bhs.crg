using System.Globalization;
using System.Text.RegularExpressions;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>Выбор сметчика, который надо запомнить: шифр → вид работы, единица и коэффициент.</summary>
public sealed record RateToRemember(RateMatchKey Key, RateMatchValue Value);

/// <summary>Что запомнилось сопоставлением — уезжает в ответе, чтобы экран сказал это словами.</summary>
/// <param name="Remembered">Сколько записано: новых и перенацеленных вместе.</param>
/// <param name="Replaced">Сколько из них заменили запомненное раньше другим выбором.</param>
/// <param name="Failed">Запомнить НЕ УДАЛОСЬ, а сопоставление при этом записано (<c>memory.failed</c>). Ноль
/// запомненного без признака читался бы как «запоминать было нечего».</param>
public sealed record RateMatchMemory(int Remembered, int Replaced, bool Failed = false)
{
    public static readonly RateMatchMemory Nothing = new(0, 0);
}

/// <summary>
/// Запомненные сопоставления шифров (change <c>plan-08-matching</c>, ТЗ PLAN-6.1): найти, запомнить, сравнить
/// единицы. По образцу <c>SupplierMatching</c> модуля <c>costs</c>.
/// </summary>
public static partial class RateMatching
{
    /// <summary>Запомненное по ключам — словарём по отпечатку.</summary>
    public static async Task<Dictionary<string, RateMatch>> FindAsync(
        PlanDbContext db, IEnumerable<RateMatchKey> keys, CancellationToken ct)
    {
        var hashes = keys.Select(k => k.Hash).Distinct().ToList();
        if (hashes.Count == 0) return [];
        return await db.RateMatches.AsNoTracking().Where(m => hashes.Contains(m.KeyHash)).ToDictionaryAsync(m => m.KeyHash, ct);
    }

    /// <summary>
    /// Одна ли это единица расценки: без регистра, пробелов и «ё». «100 м» и «100м» — одна; «100 м» и «м» —
    /// разные, и запомненное для одной к другой не подставляется (<c>unit-changed</c>).
    /// </summary>
    public static bool SameUnit(string? a, string? b) => UnitText(a) == UnitText(b);

    /// <summary>
    /// Коэффициент, который предлагают единицы: «100 м» → «м» = 100, «1000 м3» → «м3» = 1000, «шт» → «шт» = 1;
    /// <c>null</c> — единицы друг друга не объясняют, и коэффициент называет человек. Предложение, а не правило:
    /// ошибка в нём даёт план в сто раз больше факта, поэтому ответ сопоставления несёт обе единицы.
    /// </summary>
    public static decimal? SuggestFactor(string? sourceUnit, string? unitName)
    {
        var source = UnitText(sourceUnit);
        var unit = UnitText(unitName);
        if (source.Length == 0 || unit.Length == 0) return null;
        if (source == unit) return 1m;

        var scaled = Scaled().Match(source);
        return scaled.Success && scaled.Groups[2].Value == unit
            && decimal.TryParse(scaled.Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var factor) && factor > 0
            ? factor
            : null;
    }

    /// <summary>Коэффициент, названный человеком, — или предложенный единицами; иначе отказ словами.</summary>
    public static decimal FactorOf(decimal? named, string? sourceUnit, ModuleUnit unit)
    {
        if (named is { } factor)
            return factor > 0
                ? factor
                : throw new InvalidRequestException("Коэффициент пересчёта должен быть больше нуля.");
        return SuggestFactor(sourceUnit, unit.Name)
            ?? throw new InvalidRequestException(
                $"Коэффициент пересчёта не назван, а единицы его не подсказывают: расценка в «{sourceUnit ?? "без единицы"}», " +
                $"единица классификатора — «{unit.Name}». Назовите коэффициент («factor»): сколько единиц классификатора " +
                "в одной единице расценки.");
    }

    /// <summary>
    /// Оставить из запоминаемого годное: ключ, которому в одном действии назначены два разных выбора, не
    /// запоминается вовсе — какой из двух верен, знать неоткуда.
    /// </summary>
    public static IReadOnlyList<RateToRemember> Worth(IEnumerable<RateToRemember> chosen)
    {
        var all = chosen.ToList();
        var disputed = all.GroupBy(c => c.Key.Hash)
            .Where(g => g.Select(c => (c.Value.WorkTypeId, c.Value.UnitId, c.Value.Factor, UnitText(c.Value.SourceUnit))).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();
        return [.. all.Where(c => !disputed.Contains(c.Key.Hash)).DistinctBy(c => c.Key.Hash)];
    }

    /// <summary>
    /// Запомнить выбор: новое сопоставление либо замена прежнего.
    ///
    /// <para>⚠️ <b>Вне транзакции сопоставления строк, и нарочно</b> (как <c>SupplierMatching.RememberAsync</c>):
    /// таблица запомненного общая, и две сметы, сопоставляемые разом, добавляют один и тот же шифр — второй
    /// получает отказ ключа. Внутри транзакции строк этот отказ погубил бы само сопоставление; здесь он значит
    /// «кто-то успел раньше» — перечитываем и повторяем.</para>
    /// </summary>
    public static async Task<RateMatchMemory> RememberAsync(
        PlanDbContext db, IReadOnlyList<RateToRemember> chosen, IModuleUser user, CancellationToken ct)
    {
        if (chosen.Count == 0) return RateMatchMemory.Nothing;
        var hashes = chosen.Select(c => c.Key.Hash).ToList();

        for (var attempt = 0; ; attempt++)
        {
            var known = await db.RateMatches.Where(m => hashes.Contains(m.KeyHash)).ToDictionaryAsync(m => m.KeyHash, ct);
            int remembered = 0, replaced = 0;
            foreach (var (key, value) in chosen)
            {
                if (!known.TryGetValue(key.Hash, out var match))
                {
                    db.RateMatches.Add(RateMatch.Create(key, value, user.Id, user.Name));
                    remembered++;
                }
                else if (!Same(match.Value, value))
                {
                    match.Point(key.RateCode, value, user.Id, user.Name);
                    remembered++;
                    replaced++;
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return new(remembered, replaced);
            }
            catch (Exception lost) when (attempt == 0
                && lost is DbUpdateException or ConflictException { InnerException: DbUpdateConcurrencyException })
            {
                // Кто-то запомнил тот же шифр раньше или перенацелил его из списка, пока мы читали. Свои
                // несохранённые записи — из-под отслеживания, иначе повтор записал бы их снова.
                Detach(db);
            }
        }
    }

    /// <summary>
    /// Запомнить, не дав отказу запоминания стать отказом сопоставления: строки к этому мгновению записаны.
    /// Пробрось мы отказ — человек увидел бы «не сопоставлено» при сопоставленных строках, а экран остался бы
    /// со старой версией редакции. Отказ уезжает в ответе признаком, причина — в журнал сервера.
    /// </summary>
    public static async Task<RateMatchMemory> RememberSafelyAsync(
        PlanDbContext db, IReadOnlyList<RateToRemember> chosen, IModuleUser user, ILogger log, CancellationToken ct)
    {
        try
        {
            return await RememberAsync(db, chosen, user, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Detach(db);
            log.LogWarning(failure, "Сопоставления шифров не запомнены ({Count}): строки сметы сопоставлены.", chosen.Count);
            return new(0, 0, Failed: true);
        }
    }

    public static bool Same(RateMatchValue a, RateMatchValue b) =>
        a.WorkTypeId == b.WorkTypeId && a.UnitId == b.UnitId && a.Factor == b.Factor && SameUnit(a.SourceUnit, b.SourceUnit);

    private static void Detach(PlanDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<RateMatch>().ToList())
            entry.State = EntityState.Detached;
    }

    private static string UnitText(string? text) =>
        text is null ? string.Empty : string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant().Replace('ё', 'е');

    /// <summary>«100м», «1000м3», «0,1т» — множитель и единица.</summary>
    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)(\D.*)$")]
    private static partial Regex Scaled();
}
