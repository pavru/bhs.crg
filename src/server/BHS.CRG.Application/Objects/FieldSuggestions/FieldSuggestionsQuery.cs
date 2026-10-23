using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Objects.FieldSuggestions;

/// <summary>
/// Подсказки поля по ранее введённому (issue #1530, эпик #1527, контракт D10): значения поля
/// верхнего уровня записей семьи типа и строк массивов и составных полей чужих записей (issue #1532),
/// уже в порядке показа.
/// </summary>
/// <param name="TypeId">Любой тип семьи, где поле видно (решение В-2).</param>
/// <param name="Scope">Где стоит читатель — по нему решается «своя стройка». null — нигде.</param>
/// <param name="Access">От чьего имени читаются записи (D5).</param>
/// <param name="Window">Пределы окна; null — <see cref="SuggestionWindow.Default" />. Подменяет тест.</param>
public sealed record FieldSuggestionsQuery(
    Guid TypeId, string Field, string? Q, CatalogScope? Scope, Guid? ScopeId, int Limit, DataAccess Access,
    SuggestionWindow? Window = null) : IRequest<IReadOnlyList<FieldSuggestion>>;

/// <summary>
/// План источников → окно → порядок. Отказы — исключениями своего рода: неизвестные тип и поле —
/// 400, все источники закрыты правами — 403, таймаут — <see cref="SuggestionsTimedOutException" />
/// (адрес отвечает 503). Поле, которое не подсказывается, — пустой список, а не отказ: форма зовёт
/// адрес по схеме, и отказ на законный вопрос был бы шумом.
/// </summary>
public sealed class FieldSuggestionsHandler(
    IRepository<DocumentType> types,
    IRepository<Section> sections,
    IRepository<DocumentSet> sets,
    IScopeSubtree subtree,
    IFieldSuggestionReader reader) : IRequestHandler<FieldSuggestionsQuery, IReadOnlyList<FieldSuggestion>>
{
    public async Task<IReadOnlyList<FieldSuggestion>> Handle(FieldSuggestionsQuery q, CancellationToken ct)
    {
        // Без отслеживания: адрес зовут после каждой набранной буквы, и трекинг всех типов со схемами
        // на каждый вызов — память и время впустую (GetAllAsync отслеживает).
        var byId = (await types.FindAsync(_ => true, ct)).ToDictionary(t => t.Id);
        switch (SuggestionSources.For(q.TypeId, q.Field, byId, q.Access))
        {
            case SuggestionPlan.Unknown unknown: throw new InvalidRequestException(unknown.Reason);
            case SuggestionPlan.Forbidden forbidden: throw new ForbiddenException(forbidden.Reason);
            case SuggestionPlan.Read { IsEmpty: false } read:
                var here = await HereAsync(q.Scope, q.ScopeId, ct);
                var rows = await reader.ReadAsync(read.Types, q.Field, read.Nested, q.Window ?? SuggestionWindow.Default, ct);
                return SuggestionRanking.Rank(
                    rows.Select(r => new SuggestionSeen(r.RecordId, r.Value, here(r.ScopeLevel, r.ScopeId), r.UpdatedAt)),
                    q.Q, q.Limit);
            default: return [];
        }
    }

    /// <summary>
    /// «Своя стройка» читателя: стройка, её разделы и комплекты. Записи уровня «вся система» — свои
    /// для любого читателя (решение В-3): у номенклатуры стройки нет, и порог «чужих» выключил бы
    /// ровно главный выигрыш — производителя, встреченного однажды.
    ///
    /// <para>Стройку находим общим подъёмом (<see cref="ScopeAncestry" />) — тем же, что у
    /// <c>/api/common-data/for-scope</c>; комплекты под
    /// ней — общим спуском (<see cref="IScopeSubtree" />), которым считают и каскад удаления, и
    /// счётчик замечаний. Неизвестный уровень своих областей не даёт: всё, кроме системы, — чужое.</para>
    /// </summary>
    private async Task<Func<CatalogScope, Guid?, bool>> HereAsync(CatalogScope? scope, Guid? scopeId, CancellationToken ct)
    {
        var construction = (await ScopeAncestry.ResolveAsync(scope, scopeId, sets, sections, ct)).ConstructionId;
        if (construction is not { } cid) return (level, _) => level == CatalogScope.System;

        var ownSections = (await sections.FindAsync(s => s.ConstructionId == cid, ct)).Select(s => s.Id).ToHashSet();
        var ownSets = (await subtree.SetIdsUnderAsync(CatalogScope.Construction, cid, ct)).ToHashSet();
        return (level, id) => level switch
        {
            CatalogScope.System => true,
            CatalogScope.Construction => id == cid,
            CatalogScope.Section => id is { } s && ownSections.Contains(s),
            CatalogScope.Set => id is { } s && ownSets.Contains(s),
            _ => false,
        };
    }
}
