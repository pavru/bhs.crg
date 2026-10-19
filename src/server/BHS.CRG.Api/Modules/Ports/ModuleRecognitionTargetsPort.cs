using BHS.CRG.Application.Common;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Recognition;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Цели полей профиля для модуля (issue #1483) — переходник к сопоставителю ядра
/// (<see cref="RecognitionTargetMatcher" />): объявленное модулем плюс поля, которые заказчик дописал в
/// его тип. Схему типа читает ядро — модулю её не отдают (<see cref="IModuleTypes" />).
///
/// <para>Тип не заведён (проекция пропустила объявление) — остаются объявленные цели: дописанных полей
/// тогда нет и быть не может. Адреса записи модуля в этом случае отказывают сами.</para>
/// </summary>
public sealed class ModuleRecognitionTargetsPort(
    RecognitionProfileCatalog catalog, IRepository<DocumentType> types, IRepository<PrimitiveType> primitives)
    : IModuleRecognitionTargets
{
    public async Task<IReadOnlyDictionary<string, ModuleFieldTarget?>> ResolveAsync(
        string profileCode, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var declaration = catalog.Find(profileCode) ?? throw new InvalidOperationException(
            $"Профиль распознавания «{profileCode}» не объявлен ни одним модулем сборки.");
        catalog.Require(declaration.Owner, $"Профиль распознавания «{declaration.Name}»");

        IReadOnlyList<SchemaFieldInfo> fields = [];
        IReadOnlyDictionary<Guid, PrimitiveType> byPrimitive = new Dictionary<Guid, PrimitiveType>();
        if (declaration.Targets is { } targets)
        {
            var all = await types.GetAllAsync(ct);
            if (all.FirstOrDefault(t => t.Code == targets.RecordType) is { } type)
            {
                fields = DocumentTypeSchemaReader.EffectiveFields(type.Id, all.ToDictionary(t => t.Id));
                byPrimitive = (await primitives.GetAllAsync(ct)).ToDictionary(p => p.Id);
            }
        }

        return RecognitionTargetMatcher.Resolve(keys, declaration.Targets, fields, byPrimitive)
            .ToDictionary(pair => pair.Key, pair => Map(pair.Value), StringComparer.Ordinal);
    }

    private static ModuleFieldTarget? Map(ResolvedRecognitionTarget? target) => target is null
        ? null
        : new ModuleFieldTarget(target.Key, target.Origin switch
        {
            RecognitionTargetOrigin.Declared => ModuleTargetOrigin.Module,
            RecognitionTargetOrigin.Type => ModuleTargetOrigin.Type,
            RecognitionTargetOrigin.Input => ModuleTargetOrigin.Input,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Origin, "Неизвестное происхождение цели."),
        }, target.Type, target.Unavailable, target.Title);
}
