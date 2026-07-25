using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Recognition;

/// <inheritdoc />
public class RecognitionProfileProvider(AppDbContext db) : IRecognitionProfileProvider
{
    public async Task<ResolvedRecognitionProfile> GetBuiltInAsync(string code, CancellationToken ct = default)
    {
        var profile = await db.RecognitionProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, ct)
            ?? throw new InvalidOperationException(
                $"Встроенный профиль распознавания «{code}» отсутствует — не сработал сидинг при старте.");
        return RecognitionProfileJson.Resolve(profile);
    }

    public async Task<ResolvedRecognitionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await db.RecognitionProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return profile is null ? null : RecognitionProfileJson.Resolve(profile);
    }

    public async Task<ResolvedRecognitionProfile?> GetForTagAsync(string tag, CancellationToken ct = default)
    {
        var code = BuiltInRecognitionProfiles.CodeForTag(tag);
        return code is null ? null : await GetBuiltInAsync(code, ct);
    }

    public bool IsTableTag(string tag) => BuiltInRecognitionProfiles.CodeForTag(tag) is not null;

    public async Task<IReadOnlyList<RecognitionProfile>> ListByKindAsync(
        RecognitionProfileKind kind, CancellationToken ct = default)
        => await db.RecognitionProfiles.AsNoTracking()
            .Where(p => p.Kind == kind).OrderBy(p => p.Name).ToListAsync(ct);

    public RecognitionKindInfo DescribeKind(RecognitionProfileKind kind) => ToInfo(RecognitionKinds.Describe(kind));

    public IReadOnlyList<RecognitionKindInfo> ListKinds() => [.. RecognitionKinds.All.Select(ToInfo)];

    public Task ReseedBuiltInAsync(CancellationToken ct = default) => RecognitionProfileSeeder.SeedAsync(db, ct);

    private static RecognitionKindInfo ToInfo(RecognitionKindDescriptor d) => new(
        d.Kind.ToString(), d.Label, d.SupportsShape, d.HasScalarFields,
        IsTabular: d.RowsKey is not null, d.SystemFieldNames);
}
