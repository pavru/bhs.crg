using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>Параметры профиля печати так, как их присылает экран (issue #1454).</summary>
public sealed record RenditionLayoutProfileInput(
    string Name, PaperFormat Paper, PageOrientation Orientation, PageFit Fit, PageMarginsMm Margins)
{
    /// <summary>
    /// Разбор тела запроса: перечни — строками (<c>A4</c>, <c>portrait</c>, <c>width</c>), договор с
    /// экраном не зависит от того, как сервер сериализует перечни. Отказ называет поле.
    /// </summary>
    public static RenditionLayoutProfileInput Parse(
        string? name, string? paper, string? orientation, string? fit, PageMarginsMm? margins) => new(
        name ?? "",
        paper?.Trim().ToUpperInvariant() switch
        {
            "A4" => PaperFormat.A4,
            "A3" => PaperFormat.A3,
            "A5" => PaperFormat.A5,
            _ => throw PrintProfileFieldException.Of("paper", "unknown", "Бумага — A4, A3 или A5."),
        },
        orientation?.Trim().ToLowerInvariant() switch
        {
            "portrait" => PageOrientation.Portrait,
            "landscape" => PageOrientation.Landscape,
            _ => throw PrintProfileFieldException.Of("orientation", "unknown",
                "Ориентация — книжная (portrait) или альбомная (landscape)."),
        },
        fit?.Trim().ToLowerInvariant() switch
        {
            "width" => PageFit.Width,
            "none" => PageFit.None,
            _ => throw PrintProfileFieldException.Of("fit", "unknown", "Вписывание — по ширине (width) или не вписывать (none)."),
        },
        margins ?? throw PrintProfileFieldException.Of("margins", "required", "Укажите поля страницы."));
}

/// <summary>
/// Профили печати читаемого образа (issue #1454): список, правка и выбор профиля для построения.
///
/// <para>Уникальность имени держит индекс по <c>lower("Name")</c>; проверка здесь — ради понятного
/// отказа, а индекс — ради двух одновременных сохранений, которые проверку прошли бы оба.</para>
/// </summary>
public sealed class RenditionLayoutProfiles(AppDbContext db)
{
    /// <summary>Системный первым, дальше — по имени: в меню выбора «по умолчанию» стоит сверху.</summary>
    public async Task<List<RenditionLayoutProfile>> ListAsync(CancellationToken ct)
    {
        var all = await db.RenditionLayoutProfiles.AsNoTracking().ToListAsync(ct);
        return [.. all.OrderByDescending(p => p.IsSystem).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public Task<RenditionLayoutProfile?> FindAsync(Guid id, CancellationToken ct) =>
        db.RenditionLayoutProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>
    /// Профиль для построения образа: названный — он, а нет его — «не найдено», а не молча системный
    /// (человек выбрал раскладку и должен узнать, что её нет). Не названный — тот, что помнит запись
    /// вида, пока он есть, иначе системный. <c>null</c> — системного в базе нет (старт прошёл не так).
    /// </summary>
    public async Task<RenditionLayoutProfile?> ForBuildAsync(Guid? named, Guid? remembered, CancellationToken ct)
    {
        if (named is { } id)
            return await FindAsync(id, ct)
                   ?? throw new NotFoundException("Такого профиля печати нет — возможно, его удалили. Выберите другой.");
        if (remembered is { } kept && await FindAsync(kept, ct) is { } still) return still;
        return await db.RenditionLayoutProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.IsSystem, ct);
    }

    public async Task<RenditionLayoutProfile> CreateAsync(RenditionLayoutProfileInput input, CancellationToken ct)
    {
        var profile = RenditionLayoutProfile.Create(input.Name, input.Paper, input.Orientation, input.Fit, input.Margins);
        await EnsureNameFreeAsync(profile.Name, except: null, ct);
        db.RenditionLayoutProfiles.Add(profile);
        await SaveAsync(profile.Name, ct);
        return profile;
    }

    public async Task<RenditionLayoutProfile> UpdateAsync(Guid id, RenditionLayoutProfileInput input, CancellationToken ct)
    {
        var profile = await TrackedAsync(id, ct);
        profile.Update(input.Name, input.Paper, input.Orientation, input.Fit, input.Margins);
        await EnsureNameFreeAsync(profile.Name, except: id, ct);
        await SaveAsync(profile.Name, ct);
        return profile;
    }

    public async Task<RenditionLayoutProfile> ResetAsync(Guid id, CancellationToken ct)
    {
        var profile = await TrackedAsync(id, ct);
        profile.ResetToFactory();
        await db.SaveChangesAsync(ct);
        return profile;
    }

    /// <summary>
    /// Удалить свой профиль. Записи видов, построенных по нему, остаются как были: ссылка на профиль у
    /// них без внешнего ключа, и при чтении такой вид называется «профиль удалён».
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var profile = await TrackedAsync(id, ct);
        profile.EnsureDeletable();
        db.RenditionLayoutProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
    }

    private async Task<RenditionLayoutProfile> TrackedAsync(Guid id, CancellationToken ct) =>
        await db.RenditionLayoutProfiles.FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw new NotFoundException("Такого профиля печати нет — возможно, его удалили.");

    private async Task EnsureNameFreeAsync(string name, Guid? except, CancellationToken ct)
    {
        var lower = name.ToLower();
        if (await db.RenditionLayoutProfiles.AnyAsync(p => p.Name.ToLower() == lower && p.Id != except, ct))
            throw new PrintProfileNameTakenException(name);
    }

    private async Task SaveAsync(string name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbFailure.IsUniqueViolation(ex))
        {
            throw new PrintProfileNameTakenException(name, ex);
        }
    }

}

/// <summary>
/// Системный профиль печати при старте (issue #1454): создать, если его нет, — и только. Тихого
/// пересева нет нарочно: выпуск, сменивший заводские параметры, не должен менять раскладку видов без
/// решения администратора. Новые заводские доезжают кнопкой «Сбросить к заводским».
/// </summary>
public static class RenditionLayoutProfileSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.RenditionLayoutProfiles.AnyAsync(p => p.IsSystem, ct)) return;

        db.RenditionLayoutProfiles.Add(RenditionLayoutProfile.System());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        // Второй процесс успел первым — системный профиль есть, а это всё, что здесь нужно.
        catch (DbUpdateException ex) when (DbFailure.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
        }
    }
}
