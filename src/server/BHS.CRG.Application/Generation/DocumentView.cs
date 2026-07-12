using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Read-model генерации: минимальная проекция объекта, которую потребляют резолверы
/// (<see cref="IEntityResolver"/>, <see cref="IDataSetResolver"/>, <see cref="IQualityLinkResolver"/>).
/// Отвязывает hot-path генерации (merge/ref-логику) от конкретной сущности хранения
/// (issue #84, Фаза 1): после слияния CommonDataEntry+DocumentInstance в DomainObject
/// сменится лишь источник проекции — логика резолва останется прежней.
/// </summary>
public sealed record DocumentView(
    Guid Id,
    Guid DocumentSetId,
    Guid DocumentTypeId,
    JsonDocument Requisites,
    JsonDocument PluginData)
{
    public static DocumentView From(DocumentInstance instance) => new(
        instance.Id, instance.DocumentSetId, instance.DocumentTypeId,
        instance.Requisites, instance.PluginData);
}
