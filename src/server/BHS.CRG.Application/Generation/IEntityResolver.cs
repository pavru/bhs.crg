using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// C#-аналог ref/merge механизма из NewElementResolverStyles.xsl старой системы.
/// Принимает DocumentInstance, разрешает ссылки на сущности каталога,
/// возвращает собранный GenerationContext.
/// </summary>
public interface IEntityResolver
{
    Task<GenerationContext> ResolveAsync(DocumentInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Повторно разрешает $ref-ссылки в уже собранном контексте. Применяется после
    /// инъекции наборов данных, которые могут добавить ссылки на каталог ($ref) —
    /// например, в составные поля элементов массива. Идемпотентно для уже
    /// разрешённых данных.
    /// </summary>
    Task ResolveContextRefsAsync(GenerationContext ctx, Guid documentSetId, CancellationToken ct = default);

    /// <summary>
    /// Заполняет defaultValue из схемы типа документа (issue #53) для полей, у которых ещё НЕТ значения
    /// в контексте — ни из реквизитов инстанса, ни после инъекции наборов данных (привязок). Приоритет:
    /// значение инстанса > значение биндинга > defaultValue схемы > пусто. Вызывать ПОСЛЕ
    /// IDataSetResolver.InjectAsync (иначе биндинг не успеет "победить"). Только скалярные поля —
    /// составные/табличные (complex/array/doc-ref/doc-array/file/image) не трогает.
    /// </summary>
    Task ApplyDefaultsAsync(GenerationContext ctx, DocumentInstance instance, CancellationToken ct = default);
}
