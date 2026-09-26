using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Кто ссылается на позиции перечня работ (ТЗ CORE-11, issue #964). Объявляет МОДУЛЬ, спрашивает
/// ядро.
///
/// <para><b>Почему реестром, а не запросом по таблицам.</b> Ссылки на позицию живут в таблицах
/// модулей — позиции смет, задачи графика, строки отчётов, разноска затрат, — а ядро о модулях не
/// знает (ТЗ CORE-2) и запросить их не может даже теоретически: выключенный модуль своих служб не
/// регистрирует, а его таблицы при этом на месте. Поэтому направление обратное: модуль при
/// регистрации служб отдаёт свою реализацию, ядро спрашивает всех, кто отозвался.</para>
///
/// <para>⚠️ <b>Сегодня не отзывается никто</b> — модулей, ссылающихся на позицию, ещё нет (учёт
/// работ и планирование — этап 2). Это значит, что правило «удаляем только то, на что не ссылаются»
/// работает вхолостую, и проверить его можно лишь подставным держателем ссылок. Так и проверяется:
/// иначе сторож был бы зелёным всегда — ровно та ловушка, на которой уже попались в #962, где
/// «завести внешний ключ с каскадом» проверяло несуществующий ключ.</para>
/// </summary>
public interface IWorkPlanItemReferrer
{
    /// <summary>
    /// Что ссылается — словами и во множественном числе: «позиции смет», «строки отчётов
    /// монтажников». Уходит в текст отказа как есть, поэтому называет МЕСТО, где искать ссылку:
    /// одного числа человеку мало, чтобы понять, что удалять мешает.
    /// </summary>
    string What { get; }

    /// <summary>
    /// Сколько ссылок на эти позиции. Спрашивается о ГРУППЕ, а не о каждой по одной: удаление
    /// уровня уносит весь перечень стройки разом, и вопрос по каждой позиции превратился бы в
    /// запрос на строку.
    /// </summary>
    Task<int> CountAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct);
}

/// <summary>Общий для всех путей удаления опрос держателей ссылок на позиции перечня.</summary>
public static class WorkPlanItemReferences
{
    /// <summary>
    /// Кто держит ссылки на эти позиции — строками «что: сколько». Пустой список — ссылок нет.
    /// Пустой набор позиций — никого не спрашиваем: удалять нечего.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DescribeAsync(
        IEnumerable<IWorkPlanItemReferrer> referrers, IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return [];

        var found = new List<string>();
        foreach (var referrer in referrers)
        {
            var count = await referrer.CountAsync(itemIds, ct);
            if (count > 0) found.Add($"{referrer.What}: {count}");
        }
        return found;
    }

    /// <summary>
    /// Отказ, если ссылки есть. <paramref name="what" /> — что удаляют: «позицию перечня работ»,
    /// «стройку», «раздел».
    ///
    /// <para>Число в отказе обязательно (ТЗ CORE-11): «на позицию ссылаются» не говорит человеку
    /// ничего о том, сколько работы его ждёт, а «позиции смет: 3» — говорит.</para>
    /// </summary>
    public static void EnsureNone(IReadOnlyList<string> found, string what)
    {
        if (found.Count == 0) return;

        throw new ConflictException(
            $"Нельзя удалить {what}: на позиции перечня работ ссылаются — {string.Join(", ", found)}. " +
            "Позиция перечня — общая точка модулей, и удаление её из-под них оставило бы ссылки " +
            "висеть: план, факт и акты потеряли бы то, к чему относятся. Уберите ссылки в своих " +
            "модулях, после этого позицию можно удалить.");
    }
}

/// <summary>
/// Удаление позиции перечня работ (ТЗ CORE-11, issue #964).
///
/// <para>⚠️ Адреса у этой команды нет, и это не упущение: позиции заводит сервер по «найди или
/// создай» с первым потребителем — импортом сметы в этапе 3 (CORE-12), и человеку в этапе 1 удалять
/// нечего. Команда существует потому, что правило «удаляется только без ссылок» относится к самому
/// перечню, а не к экрану: появись дверь раньше правила, она пришла бы без него.</para>
/// </summary>
public sealed record DeleteWorkPlanItemCommand(Guid Id) : IRequest;

public sealed class WorkPlanItemHandlers(
    IRepository<WorkPlanItem> repo, IEnumerable<IWorkPlanItemReferrer> referrers)
    : IRequestHandler<DeleteWorkPlanItemCommand>
{
    public async Task Handle(DeleteWorkPlanItemCommand cmd, CancellationToken ct)
    {
        var item = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();

        WorkPlanItemReferences.EnsureNone(
            await WorkPlanItemReferences.DescribeAsync(referrers, [cmd.Id], ct),
            "позицию перечня работ");

        repo.Remove(item);
        await repo.SaveChangesAsync(ct);
    }
}
