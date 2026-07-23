namespace BHS.CRG.Application.Generation;

/// <summary>
/// C#-аналог ref/merge механизма из NewElementResolverStyles.xsl старой системы.
/// Принимает <see cref="DocumentView"/>, разрешает ссылки на сущности каталога,
/// возвращает собранный GenerationContext.
/// </summary>
public interface IEntityResolver
{
    Task<GenerationContext> ResolveAsync(DocumentView instance, CancellationToken ct = default);

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
    Task ApplyDefaultsAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default);

    /// <summary>
    /// Резолвит enum-поля реквизитов из кода (хранится в контексте) в отображаемое имя EnumType
    /// (issue #59) — иначе в PDF попадёт сырой код вместо человекочитаемого текста. Вызывать ПОСЛЕ
    /// ApplyDefaultsAsync (иначе default-значение enum-поля не получит резолва). Толерантно: код без
    /// совпадения в реестре остаётся как есть. Только верхнеуровневые скалярные поля.
    /// </summary>
    Task ResolveEnumLabelsAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default);

    /// <summary>
    /// Вычисляет расчётные поля (issue #368, фаза 1 — верхний уровень) и инжектит их значения в контекст.
    /// Вызывать ПОСЛЕ ResolveEnumLabels/ResolveContextRefs (входы формул финальны), ДО ScanMissingRequired/
    /// TypeStamper. Диагностики: цикл → Error, ошибка выражения → Warning (генерацию не блокирует).
    /// </summary>
    Task ResolveComputedFieldsAsync(GenerationContext ctx, DocumentView instance,
        List<ResolutionDiagnostic> diagnostics, CancellationToken ct = default);
}
