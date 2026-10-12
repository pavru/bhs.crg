using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Пакетная очистка записей, занятых только данными выключенных или снятых модулей (issue #1245).
///
/// <para>Удаление одной такой записи (issue #1187) — отказ, диалог и ввод числа на каждую; «очистить
/// базу» — это десятки записей. Пакет — то же действие над многими сразу, и правила у него те же:
/// решает <see cref="RecordHoldings.Release" />, отказы ядра не обходятся, число вводит человек.</para>
///
/// <para>Решения владельца (12.10.2026): человек подтверждает ОБЩЕЕ число теряемых ссылок; пакет
/// удаляется целиком или не удаляется вовсе.</para>
/// </summary>
public static class RecordBatchPurge
{
    /// <summary>
    /// Сколько записей в одном пакете. Удаляемое человек обязан мочь прочесть до ввода числа, а
    /// удаление идёт одной транзакцией. Больше — следующим заходом.
    /// </summary>
    public const int Limit = 200;
}

/// <summary>Готовый отбор: записи типа на уровне — то, что открыто на экране.</summary>
public sealed record PurgeBatchScope(Guid TypeId, CatalogScope Scope, Guid? ScopeId);

/// <summary>
/// Что войдёт в пакет и что нет. Источников два: готовый отбор (<paramref name="Of" /> — действующие
/// записи типа на уровне; архивные в него не входят) или названные записи (<paramref name="Ids" /> —
/// выбор галочками; архивные тоже).
/// </summary>
public sealed record PreviewRecordPurgeQuery(PurgeBatchScope? Of, IReadOnlyCollection<Guid>? Ids)
    : IRequest<PurgeBatchPreview>;

/// <param name="Seen">
/// Записи пакета и число ссылок у каждой — как их увидел человек. Список, а не «всё, что найдётся»:
/// запись, ставшая годной после предпросмотра, невиденной не удаляется.
/// </param>
/// <param name="References">Общее число теряемых ссылок, которое человек ввёл.</param>
public sealed record PurgeHeldRecordsCommand(IReadOnlyDictionary<Guid, int> Seen, int References)
    : IRequest<PurgedBatch>;

public sealed record PurgeBatchRecord(Guid Id, string Name, bool Archived, DormantRelease Release);

/// <summary>Запись, которая в пакет не вошла, — с причиной словами для человека.</summary>
public sealed record PurgeBatchExcluded(Guid Id, string Name, string Reason);

/// <param name="Checked">Сколько записей проверено: все записи типа на уровне или все названные.</param>
/// <param name="Missing">Сколько из названных записей уже нет.</param>
/// <param name="More">Сколько годных записей не поместилось в пакет (только у готового отбора).</param>
public sealed record PurgeBatchPreview(
    IReadOnlyList<PurgeBatchRecord> Records, IReadOnlyList<PurgeBatchExcluded> Excluded,
    int Checked, int Missing, int More)
{
    public int References => Records.Sum(r => r.Release.References);
    public int Untraceable => Records.Sum(r => r.Release.Untraceable);
}

public sealed record PurgedBatch(int Records, int References, int Untraceable);

/// <summary>
/// Пакет изменился между предпросмотром и удалением — не удалено ничего. Со свежим предпросмотром:
/// экран показывает его заново, как одиночное удаление показывает свежее число.
/// </summary>
public sealed class PurgeBatchChangedException(string message, PurgeBatchPreview fresh) : ConflictException(message)
{
    public PurgeBatchPreview Fresh { get; } = fresh;
}
