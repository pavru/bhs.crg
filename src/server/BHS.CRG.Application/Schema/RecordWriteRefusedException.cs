using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Запись отвергнута охраной (issue #957). Род отказа — «данные запроса недопустимы» (400): ничего
/// не конфликтовало и никто не занят, прислали негодное значение.
/// </summary>
public class RecordWriteRefusedException(IReadOnlyList<AuditIssue> issues)
    : InvalidRequestException(Text(issues)), IDetailedRefusal
{
    public IReadOnlyList<RefusalDetail> Details { get; } =
        [.. issues.Select(i => new RefusalDetail(i.Code, i.Path, i.Message))];

    /// <summary>
    /// Текст называет ПОЛЯ, а не только факт отказа: «готово» задачи требует указать поле, и
    /// сообщение обязано выполнять это само — там, где адреса разобрать некому (журнал задачи,
    /// уведомление, чужой клиент), остаётся только оно.
    /// </summary>
    private static string Text(IReadOnlyList<AuditIssue> issues) =>
        "Запись не сохранена. " + string.Join(" ", issues.Select(i => i.Message));
}

/// <summary>
/// Охрана записи у адреса сохранения: прочитать справочники схемы и отказать, если охрана против.
///
/// ⚠️ Отдельной точкой, а не поведением конвейера MediatR: печатная форма пишет данные ПРЯМО в слое
/// API (<c>PrintFormEndpoints</c>), мимо MediatR, и «поведение на командах» выглядело бы тотальной
/// охраной, не будучи ею. Перечень охраняемых и намеренно не охраняемых путей — в доккомментарии
/// <see cref="RecordWriteGuard" />.
/// </summary>
public static class WriteGuard
{
    /// <summary>Пустой документ для создания: там «как лежит» — ничего.</summary>
    private static readonly JsonDocument Nothing = JsonDocument.Parse("{}");

    /// <param name="stored">Данные, как они лежат. Для создания — <c>null</c>.</param>
    public static async Task EnsureAllowedAsync(
        JsonDocument? stored, JsonDocument incoming, Guid typeId,
        IRepository<DocumentType> types, IRepository<PrimitiveType> primitives, CancellationToken ct)
    {
        // Дешёвый выход прежде тяжёлого чтения: у типа без родителя собственный уровень и есть
        // эффективный, а таких типов сегодня все 70 из 70. Полный справочник читается только там,
        // где охрана действительно работает.
        var type = await types.GetByIdAsync(typeId, ct);
        if (type is null || (type.EditLevel == SchemaEditLevel.Open && type.ParentId is null)) return;

        var byId = (await types.GetAllAsync(ct)).ToDictionary(t => t.Id);
        if (RecordWriteGuard.EffectiveLevel(typeId, byId) == SchemaEditLevel.Open) return;

        var primitivesById = (await primitives.GetAllAsync(ct)).ToDictionary(p => p.Id);
        var was = (stored ?? Nothing).RootElement;
        var refusals = RecordWriteGuard.Refusals(was, incoming.RootElement, typeId, byId, primitivesById);
        if (refusals.Count > 0) throw new RecordWriteRefusedException(refusals);
    }
}
