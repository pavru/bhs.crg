using BHS.CRG.Application.Branding;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Branding;

/// <summary>
/// Фирменное оформление экземпляра (ТЗ CORE-25.1, issue #967): название — настройкой экземпляра,
/// логотип — системным ассетом шаблонов.
///
/// <para>Двух хранилищ у логотипа нет нарочно. Требование «логотип доступен шаблонам Typst как ассет
/// уровня системы» выполняется тем, что логотип И ЕСТЬ такой ассет: генерация кладёт его в
/// <c>assets/</c> тем же проходом, что остальные картинки, и печатная форма ставит его сама. Своя
/// таблица рядом означала бы две копии одного файла, из которых печать рано или поздно взяла бы
/// устаревшую, и вторую дорогу в резервную копию — обе уже есть у ассетов.</para>
///
/// <para>⚠️ Следствие, которое стоит знать: логотип виден и в списке системных ассетов на экране
/// шаблонов, и его можно удалить оттуда. Это не дефект, а цена одного хранилища: строка там — тот же
/// самый файл, а не его отражение.</para>
/// </summary>
public class BrandingService(
    AppDbContext db,
    IBlobStorage blob,
    IAppSettingsStore settings,
    IMemoryCache cache,
    BrandIconStore icons,
    ILogger<BrandingService> logger) : IBrandingService
{
    /// <summary>
    /// Сколько держим содержимое логотипа в памяти. Адрес картинки АНОНИМНЫЙ и зовётся со страницы
    /// входа, то есть до всякой проверки прав: без кеша каждый такой запрос — обращение к
    /// хранилищу за файлом до пяти мегабайт (ревью PR #1061). Ключ содержит метку версии, поэтому
    /// заменённый логотип не может прийти из кеша — придёт промах по новому ключу.
    /// </summary>
    private static readonly TimeSpan LogoCacheFor = TimeSpan.FromMinutes(30);
    public async Task<BrandingInfo> GetAsync(CancellationToken ct = default)
    {
        var name = await settings.GetAsync(AppSettingKeys.ProductName, ct);
        var logo = await LogoAssetAsync(ct, tracked: false);
        return new BrandingInfo(
            string.IsNullOrWhiteSpace(name) ? BrandingDefaults.ProductName : name,
            IsCustom: !string.IsNullOrWhiteSpace(name),
            HasLogo: logo is not null,
            LogoVersion: logo is null ? null : Version(logo),
            Icons: IconsInfo(logo, await IconsOfAsync(logo, ct)));
    }

    public async Task<BrandIcon> GetIconAsync(BrandIconKind kind, CancellationToken ct = default)
    {
        var logo = await LogoAssetAsync(ct, tracked: false);
        if (logo is null || (await IconsOfAsync(logo, ct))?.Set is not { } set)
            return StandardBrandIcons.Get(kind);

        var version = IconsVersion(logo, set);
        return new BrandIcon(set.Icons[kind], "image/png", $"\"{version}-{BrandIconKinds.Route(kind)}\"", version);
    }

    /// <summary>
    /// Метка версии построенных значков: логотип И отпечаток самих значков. Одной метки логотипа
    /// мало — см. <see cref="BrandIconSet" />: картинки меняются и без смены логотипа.
    /// </summary>
    private static string IconsVersion(TemplateAsset logo, BrandIconSet set) => $"{Version(logo)}-{set.Stamp}";

    /// <summary>
    /// Значки действующего логотипа; <c>null</c> — логотипа нет. Построены ли они уже, решает
    /// <see cref="BrandIconStore" />: при загрузке логотипа, а после перезапуска или восстановления
    /// копии — при первом обращении, без особого случая здесь.
    /// </summary>
    private async Task<BrandIconBuild?> IconsOfAsync(TemplateAsset? logo, CancellationToken ct) =>
        logo is null
            ? null
            : await icons.EnsureAsync(
                // Версии одной мало: ассет логотипа могли завести заново с той же меткой времени
                // (восстановление копии) — а строка и файл у него будут другие.
                $"{logo.Id:N}:{Version(logo)}:{logo.BlobPath}",
                // ⚠️ Чтение может пережить этот запрос (повтор после сбоя идёт в фоне): оно
                // держится только за хранилище, кеш и саму строку ассета — контекст базы ему нельзя.
                async c => (await ContentAsync(logo, c))?.Content, ct);

    private static BrandIconsInfo IconsInfo(TemplateAsset? logo, BrandIconBuild? build)
    {
        if (logo is null || build is null)
            return new(BrandIconsState.Standard, StandardBrandIcons.Version, null, null);
        return build.Set is { } set
            ? new(BrandIconsState.Custom, IconsVersion(logo, set), null, Math.Round(set.Aspect, 3))
            // Метка — стандартная: отдаются стандартные картинки.
            : new(BrandIconsState.Failed, StandardBrandIcons.Version, build.Problem ?? BrandIconProblem.Builder, null);
    }

    public Task SetProductNameAsync(string? name, CancellationToken ct = default) =>
        settings.SetAsync(AppSettingKeys.ProductName,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(), ct);

    public async Task<BrandingLogo?> GetLogoAsync(CancellationToken ct = default)
    {
        var asset = await LogoAssetAsync(ct, tracked: false);
        return asset is null ? null : await ContentAsync(asset, ct);
    }

    private async Task<BrandingLogo?> ContentAsync(TemplateAsset asset, CancellationToken ct)
    {
        if (cache.TryGetValue<BrandingLogo>(CacheKey(asset), out var cached) && cached is not null)
            return cached;

        byte[] content;
        try
        {
            await using var stream = await blob.DownloadAsync(asset.BlobPath, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            content = ms.ToArray();
        }
        // Отмену сверяем с токеном вызывающего: «человек ушёл» — отмена, а истёкший срок самого
        // хранилища — такой же отказ чтения, как любой другой, и отвечать на него надо так же.
        catch (Exception ex) when (!HttpFailure.IsUserCancellation(ex, ct))
        {
            // Строка ассета есть, файла нет: хранилище недоступно или файл убрали мимо системы.
            // Отвечаем «логотипа нет», а не отказом: логотип стоит на СТРАНИЦЕ ВХОДА, и уронить
            // вход из-за картинки — цена, несоизмеримая с потерей. Причина — в журнал.
            logger.LogWarning(ex, "Логотип компании не читается из хранилища: {BlobPath}", asset.BlobPath);
            return null;
        }

        var logo = new BrandingLogo(content, asset.MimeType, asset.FileName, $"\"{Version(asset)}\"");
        cache.Set(CacheKey(asset), logo, LogoCacheFor);
        return logo;
    }

    /// <summary>Ключ кеша содержит метку версии: замена файла делает прежний ключ недостижимым.</summary>
    private static string CacheKey(TemplateAsset asset) => $"branding-logo:{Version(asset)}";

    public async Task<BrandingInfo> SetLogoAsync(
        byte[] content, string fileName, string mimeType, CancellationToken ct = default)
    {
        // Имя файла — наше (см. BrandingDefaults.LogoFileName): оно попадает в путь компиляции
        // шаблона, и регистр там значим.
        fileName = BrandingDefaults.LogoFileName(Path.GetExtension(fileName));
        var blobPath = await blob.UploadAsync(fileName, new MemoryStream(content), mimeType, ct);
        var asset = await LogoAssetAsync(ct);

        string? previous = null;
        if (asset is null)
        {
            asset = TemplateAsset.Create(
                TemplateAssetScope.System, null, TemplateAssetKind.Image,
                BrandingDefaults.LogoAssetName, fileName, mimeType, blobPath);
            db.TemplateAssets.Add(asset);
        }
        else
        {
            previous = asset.BlobPath;
            asset.Replace(fileName, mimeType, blobPath, fontFamilyName: null);
        }

        await db.SaveChangesAsync(ct);
        // Перечитываем строку: метка версии — момент замены, а база хранит его грубее, чем память
        // (микросекунды против ста наносекунд). Без этого ответ загрузки называл бы одну метку, а
        // следующее чтение — другую: значки строились бы дважды, а открытая вкладка увидела бы
        // «смену» логотипа, которой не было (issue #1315).
        await db.Entry(asset).ReloadAsync(ct);

        // Старый файл убираем ПОСЛЕ успешной записи новой строки: наоборот — значит на миг
        // остаться без логотипа и с ссылкой в никуда, если запись не удалась.
        if (previous is not null) await TryDeleteAsync(previous, ct);

        // Значки строятся здесь же, до ответа (GetAsync): администратор сразу видит, получились ли.
        return await GetAsync(ct);
    }

    public async Task<BrandingInfo> RemoveLogoAsync(CancellationToken ct = default)
    {
        var asset = await LogoAssetAsync(ct);
        if (asset is not null)
        {
            db.TemplateAssets.Remove(asset);
            await db.SaveChangesAsync(ct);
            await TryDeleteAsync(asset.BlobPath, ct);
        }
        return await GetAsync(ct);
    }

    /// <summary>
    /// Ассет логотипа — системного уровня и с зарезервированным именем. Берём ПЕРВЫЙ по дате
    /// обновления: имя в таблице ничем не уникально, и два ассета с ним завести можно руками через
    /// экран шаблонов. Выбирать «какой-нибудь» нельзя — логотип менялся бы от запроса к запросу.
    ///
    /// <para><paramref name="tracked" /> — только замене и удалению: им строку менять. Чтения идут
    /// без отслеживания — адреса значков и манифеста анонимные и зовутся каждой загрузкой страницы.
    /// Запоминать сам ответ на секунды не стали нарочно: логотип меняется и мимо этой службы (экран
    /// ассетов шаблонов, восстановление копии), и запомненная строка отдавала бы прежние значки под
    /// прежней меткой — ровно то, от чего ключ значков с версией и защищает.</para>
    /// </summary>
    private Task<TemplateAsset?> LogoAssetAsync(CancellationToken ct, bool tracked = true) =>
        (tracked ? db.TemplateAssets : db.TemplateAssets.AsNoTracking())
            .Where(a => a.Scope == TemplateAssetScope.System
                        && a.Kind == TemplateAssetKind.Image
                        && a.Name == BrandingDefaults.LogoAssetName)
            .OrderByDescending(a => a.UpdatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>Метка версии файла — момент последней замены, в виде, годном для URL и ETag.</summary>
    private static string Version(TemplateAsset asset) => asset.UpdatedAt.UtcTicks.ToString();

    private async Task TryDeleteAsync(string blobPath, CancellationToken ct)
    {
        // Файл, оставшийся в хранилище, — мусор, а не поломка: его подберёт уборка осиротевших
        // блобов. Уронить из-за него замену логотипа значило бы поменять малое на большое.
        try { await blob.DeleteAsync(blobPath, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Прежний файл логотипа не удалён из хранилища: {BlobPath}", blobPath);
        }
    }
}
