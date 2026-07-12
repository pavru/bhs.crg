using System.Text.Json;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Read-model генерации: минимальная проекция объекта, которую потребляют резолверы
/// (<see cref="IEntityResolver"/>, <see cref="IDataSetResolver"/>, <see cref="IQualityLinkResolver"/>).
/// Отвязывает hot-path генерации (merge/ref-логику) от конкретной сущности хранения
/// (issue #84, Фаза 1): источник проекции сменился на единый <see cref="DomainObject"/>,
/// а логика резолва осталась прежней.
/// </summary>
public sealed record DocumentView(
    Guid Id,
    Guid DocumentSetId,
    Guid DocumentTypeId,
    JsonDocument Requisites,
    JsonDocument PluginData)
{
    /// <summary>Проекция объекта-документа. Для документа расположение — (Set, ScopeId=setId),
    /// а <see cref="DomainObject.PluginData"/> берётся из документной фасеты.</summary>
    public static DocumentView From(DomainObject o) => new(
        o.Id, o.ScopeId ?? Guid.Empty, o.CompositeTypeId, o.Data, o.PluginData);
}
