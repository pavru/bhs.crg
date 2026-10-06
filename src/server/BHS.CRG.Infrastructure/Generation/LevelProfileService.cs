using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Ленивое создание объекта-профиля уровня (issue #258). Идемпотентно: если профиль-тип не задан или
/// контейнер уже несёт профиль — no-op. Иначе создаёт пустой DomainObject профиль-типа на scope
/// контейнера и проставляет FK. Вызывается при обращении к общим данным уровня (CommonDataHandlers).
/// </summary>
public class LevelProfileService(AppDbContext db) : ILevelProfileService
{
    public async Task<Guid?> EnsureProfileAsync(CatalogScope level, Guid containerId, CancellationToken ct = default)
    {
        var tag = LevelProfiles.TagFor(level);
        if (tag is null) return null; // не контейнерный уровень (System)

        var types = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var typeId = LevelProfiles.ResolveProfileTypeId(types, tag);
        if (typeId is null) return null; // профиль-тип не сконфигурирован
        // Профиль — объект общей таблицы. Тип, чьи записи модуль держит у себя, профилем не служит
        // (issue #1215): заведённый здесь объект был бы тем самым, что общим путём не заводится.
        // Не отказ, а «профиля нет»: служба зовётся и на чтении страницы уровня.
        if (!TypeStorageRules.KeptInCommonTable(types.First(t => t.Id == typeId.Value))) return null;

        // Текущий FK контейнера (tracked — понадобится проставить).
        var currentFk = await GetContainerProfileIdAsync(level, containerId, ct);
        if (currentFk is { } fk && await db.DomainObjects.AsNoTracking().AnyAsync(o => o.Id == fk, ct))
            return fk; // валидный профиль уже есть

        // Мог быть объект профиль-типа на этом scope без FK (напр. создан вручную) — переиспользуем.
        // Архивную запись под профиль не берём (issue #1185): назначение профилем — тот же выбор, а
        // архивного профиля действие не допускает; иначе он появился бы здесь, обходным путём.
        var existing = await db.DomainObjects.FirstOrDefaultAsync(
            o => o.ScopeLevel == level && o.ScopeId == containerId && o.CompositeTypeId == typeId.Value
                 && o.ArchivedAt == null, ct);

        var profile = existing;
        if (profile is null)
        {
            profile = DomainObject.Create(typeId.Value, null, JsonDocument.Parse("{}"), level, containerId);
            db.DomainObjects.Add(profile);
        }
        await SetContainerProfileAsync(level, containerId, profile.Id, ct);
        await db.SaveChangesAsync(ct);
        return profile.Id;
    }

    private async Task<Guid?> GetContainerProfileIdAsync(CatalogScope level, Guid id, CancellationToken ct) => level switch
    {
        CatalogScope.Construction => (await db.Constructions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct))?.ProfileObjectId,
        CatalogScope.Section => (await db.Sections.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct))?.ProfileObjectId,
        CatalogScope.Set => (await db.DocumentSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct))?.ProfileObjectId,
        _ => null,
    };

    private async Task SetContainerProfileAsync(CatalogScope level, Guid id, Guid objectId, CancellationToken ct)
    {
        switch (level)
        {
            case CatalogScope.Construction:
                (await db.Constructions.FirstOrDefaultAsync(c => c.Id == id, ct))?.SetProfileObject(objectId);
                break;
            case CatalogScope.Section:
                (await db.Sections.FirstOrDefaultAsync(s => s.Id == id, ct))?.SetProfileObject(objectId);
                break;
            case CatalogScope.Set:
                (await db.DocumentSets.FirstOrDefaultAsync(s => s.Id == id, ct))?.SetProfileObject(objectId);
                break;
        }
    }
}
