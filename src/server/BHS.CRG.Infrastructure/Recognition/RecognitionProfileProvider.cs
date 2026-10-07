using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Recognition;

/// <inheritdoc />
public class RecognitionProfileProvider(AppDbContext db, RecognitionProfileCatalog catalog) : IRecognitionProfileProvider
{
    public async Task<ResolvedRecognitionProfile> GetDefaultAsync(
        RecognitionProfileKind kind, CancellationToken ct = default)
        => await LoadAsync(catalog.Require(kind), ct);

    public void RequireKind(RecognitionProfileKind kind) => catalog.Require(kind);

    public async Task<ResolvedRecognitionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await db.RecognitionProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null) return null;
        // Ворота. Профиль выключенного модуля не «не найден»: по «не найден» потребитель молча
        // взял бы заводской — и прочитал бы документ не теми параметрами, которые выбрал человек.
        catalog.Require(profile);
        return RecognitionProfileJson.Resolve(profile);
    }

    public async Task<ResolvedRecognitionProfile?> GetForTagAsync(string tag, CancellationToken ct = default)
    {
        if (catalog.ForTag(tag) is not { } declaration) return null;
        catalog.Require(declaration.Owner, $"Профиль распознавания «{declaration.Name}»");
        return await LoadAsync(declaration, ct);
    }

    // ⚠️ Оба предиката ворот НЕ спрашивают — нарочно. По ним решается «источник осиротел», и там по
    // ответу УДАЛЯЮТСЯ данные: ответь выключенный модуль «это не таблица», и его выключение снесло бы
    // источники таблиц. Выключение модуля данных не трогает; ворота стоят там, где профиль берут,
    // чтобы распознавать.
    public bool IsTableTag(string tag) => catalog.ForTag(tag) is not null;

    public async Task<bool> IsTableGroupAsync(
        Guid? profileId, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        // Привязанный профиль перекрывает тэг: он и есть способ сделать табличной группу, у которой
        // функционального тэга нет и быть не может (произвольная таблица).
        if (profileId is { } pid)
        {
            var profile = await db.RecognitionProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct);
            // Профиль удалён — деградируем к тэгу, а не считаем группу нетабличной: иначе удаление
            // профиля молча снесло бы источники, которые ещё держатся на тэге.
            if (profile is not null) return RecognitionKinds.Describe(profile.Kind).RowsKey is not null;
        }
        return (tags ?? []).Any(IsTableTag);
    }

    public RecognitionKindInfo DescribeKind(RecognitionProfileKind kind) => ToInfo(RecognitionKinds.Describe(kind));

    public IReadOnlyList<RecognitionKindInfo> ListKinds() =>
        [.. RecognitionKinds.All.Where(d => catalog.IsAvailable(d.Kind)).Select(ToInfo)];

    public Task ReseedBuiltInAsync(CancellationToken ct = default) => RecognitionProfileSeeder.SeedAsync(db, catalog, ct);

    private async Task<ResolvedRecognitionProfile> LoadAsync(
        RecognitionProfileDeclaration declaration, CancellationToken ct)
    {
        var profile = await db.RecognitionProfiles.AsNoTracking()
                          .FirstOrDefaultAsync(p => p.Code == declaration.Code, ct)
            ?? throw new InvalidOperationException(
                $"Встроенный профиль распознавания «{declaration.Code}» отсутствует — не сработал сидинг при старте.");
        return RecognitionProfileJson.Resolve(profile);
    }

    private RecognitionKindInfo ToInfo(RecognitionKindDescriptor d)
    {
        var owner = catalog.OwnerOfKind(d.Kind);
        return new(
            d.Kind.ToString(), d.Label, d.SupportsShape, d.HasScalarFields,
            IsTabular: d.RowsKey is not null, d.SystemFieldNames, d.Scope.ToString(),
            Module: owner?.Code, ModuleTitle: owner?.Title);
    }
}
