using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Порядок привязок наборов данных и правило «кто из двух в одно поле применяется» — одно на всех, кто
/// их читает (issue #1402, ревью PR #1451): генерацию, предпросмотр, список на экране и срез документа
/// для агента.
///
/// <para>Порядок — время создания, при равенстве идентификатор; копия время создания переносит, так
/// что он один на любой базе. Без него каждое чтение шло в порядке строк в куче, и после pg_restore
/// менялось. Пока порядок закрепляла одна генерация, срез для агента брал ПЕРВУЮ из двух привязок в
/// поле — ту, что в PDF проигрывает: агенту называлась одна таблица, печаталась другая.</para>
/// </summary>
public static class DataSetBindingOrder
{
    /// <summary>Привязки в закреплённом порядке. Только в запросе к базе: сравнение идентификаторов в
    /// памяти у .NET и у PostgreSQL разное, и порядок «в памяти» разошёлся бы с порядком генерации.</summary>
    public static IQueryable<DataSetBinding> InBindingOrder(this IQueryable<DataSetBinding> bindings)
        => bindings.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id);

    /// <summary>
    /// Привязки, которые не применяются, и та, что применяется вместо каждой: в одно табличное поле
    /// пишут несколько, и запись в контекст перезаписывает значение — побеждает позже заведённая.
    ///
    /// <para>Называть проигравшую обязательно: молча она скачивала свой файл, разбирала строки, а
    /// результат затирался — документ печатал одну таблицу, и ни предпросмотр, ни отчёт генерации не
    /// говорили, что вторая не работает. Скалярные привязки сюда не входят: они пишут поля своим
    /// маппингом, и одно целевое поле на всю привязку у них не названо.</para>
    /// </summary>
    /// <param name="ordered">Привязки одного владельца в порядке <see cref="InBindingOrder" />.</param>
    public static IReadOnlyDictionary<Guid, DataSetBinding> Overridden(IReadOnlyList<DataSetBinding> ordered)
        => ordered
            .Where(b => b.TargetFieldKey is not null)
            .GroupBy(b => b.TargetFieldKey!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.SkipLast(1).Select(loser => (loser, winner: g.Last())))
            .ToDictionary(p => p.loser.Id, p => p.winner);

    /// <summary>Почему привязка не применяется — одними словами в предпросмотре и в отчёте генерации.</summary>
    public static string OverriddenText(DataSetBinding loser, DataSetBinding winner)
        => $"Привязка источника «{loser.Source.Name}» не применяется: в поле «{loser.TargetFieldKey}» " +
           $"пишет и привязка источника «{winner.Source.Name}», заведённая позже, — в документ идут её " +
           "данные. Удалите лишнюю привязку.";
}
