using BHS.CRG.Application.Branding;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    ILogger<BrandingService> logger) : IBrandingService
{
    public async Task<BrandingInfo> GetAsync(CancellationToken ct = default)
    {
        var name = await settings.GetAsync(AppSettingKeys.ProductName, ct);
        var logo = await LogoAssetAsync(ct);
        return new BrandingInfo(
            string.IsNullOrWhiteSpace(name) ? BrandingDefaults.ProductName : name,
            IsCustom: !string.IsNullOrWhiteSpace(name),
            HasLogo: logo is not null,
            LogoVersion: logo is null ? null : Version(logo));
    }

    public Task SetProductNameAsync(string? name, CancellationToken ct = default) =>
        settings.SetAsync(AppSettingKeys.ProductName,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(), ct);

    public async Task<BrandingLogo?> GetLogoAsync(CancellationToken ct = default)
    {
        var asset = await LogoAssetAsync(ct);
        if (asset is null) return null;

        byte[] content;
        try
        {
            await using var stream = await blob.DownloadAsync(asset.BlobPath, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            content = ms.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Строка ассета есть, файла нет: хранилище недоступно или файл убрали мимо системы.
            // Отвечаем «логотипа нет», а не отказом: логотип стоит на СТРАНИЦЕ ВХОДА, и уронить
            // вход из-за картинки — цена, несоизмеримая с потерей. Причина — в журнал.
            logger.LogWarning(ex, "Логотип компании не читается из хранилища: {BlobPath}", asset.BlobPath);
            return null;
        }

        return new BrandingLogo(content, asset.MimeType, asset.FileName, $"\"{Version(asset)}\"");
    }

    public async Task<BrandingInfo> SetLogoAsync(
        byte[] content, string fileName, string mimeType, CancellationToken ct = default)
    {
        var blobPath = await blob.UploadAsync(fileName, new MemoryStream(content), mimeType, ct);
        var asset = await LogoAssetAsync(ct);

        if (asset is null)
        {
            db.TemplateAssets.Add(TemplateAsset.Create(
                TemplateAssetScope.System, null, TemplateAssetKind.Image,
                BrandingDefaults.LogoAssetName, fileName, mimeType, blobPath));
        }
        else
        {
            // Старый файл убираем ПОСЛЕ успешной записи новой строки: наоборот — значит на миг
            // остаться без логотипа и с ссылкой в никуда, если запись не удалась.
            var previous = asset.BlobPath;
            asset.Replace(fileName, mimeType, blobPath, fontFamilyName: null);
            await db.SaveChangesAsync(ct);
            await TryDeleteAsync(previous, ct);
            return await GetAsync(ct);
        }

        await db.SaveChangesAsync(ct);
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
    /// </summary>
    private Task<TemplateAsset?> LogoAssetAsync(CancellationToken ct) =>
        db.TemplateAssets
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
