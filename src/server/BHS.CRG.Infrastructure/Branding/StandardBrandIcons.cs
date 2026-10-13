using System.Collections.Frozen;
using System.Security.Cryptography;
using BHS.CRG.Application.Branding;

namespace BHS.CRG.Infrastructure.Branding;

/// <summary>
/// Стандартные значки — те, что экземпляр показывает без логотипа (issue #1315).
///
/// <para>Лежат ЗДЕСЬ, вшитыми в сборку, а не файлами клиента. Значок отдаёт сервер — и построенный,
/// и стандартный, одним адресом: страница ссылается на адрес, а не на файл, и не знает, загружен ли
/// логотип. Образ API собирается только из <c>src/server</c> и файлов клиента не видит; оставь мы
/// копию ещё и в <c>src/client/public</c>, у стандартного значка стало бы две правды, из которых
/// обновляли бы одну.</para>
///
/// <para>Картинки делает <c>docs/tools/brand-icons.mjs</c> из мастера <c>docs/brand</c>; значок
/// вкладки — SVG, он сам светлеет на тёмной вкладке.</para>
/// </summary>
public static class StandardBrandIcons
{
    private static readonly FrozenDictionary<BrandIconKind, BrandIcon> Icons =
        new Dictionary<BrandIconKind, (string File, string MimeType)>
        {
            [BrandIconKind.Tab] = ("tab.svg", "image/svg+xml"),
            [BrandIconKind.Apple] = ("apple.png", "image/png"),
            [BrandIconKind.App192] = ("app-192.png", "image/png"),
            [BrandIconKind.App512] = ("app-512.png", "image/png"),
            [BrandIconKind.Maskable512] = ("maskable-512.png", "image/png"),
        }.ToFrozenDictionary(p => p.Key, p => Load(p.Value.File, p.Value.MimeType));

    public static BrandIcon Get(BrandIconKind kind) => Icons[kind];

    private static BrandIcon Load(string file, string mimeType)
    {
        // Имя ресурса задано в csproj (LogicalName), а не выведено из папок: вывод зависит от
        // пространства имён проекта и молча меняется при переносе файла.
        using var stream = typeof(StandardBrandIcons).Assembly.GetManifestResourceStream("brand-icons/" + file)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var content = ms.ToArray();
        // Тег — по содержимому: заменили картинку в сборке — браузер перечитает её сам, без ручной
        // метки версии, которую раньше поднимали в index.html и в манифесте (issue #728).
        var tag = Convert.ToHexStringLower(SHA256.HashData(content))[..16];
        return new BrandIcon(content, mimeType, $"\"std-{tag}\"", BrandIconsInfo.StandardVersion);
    }
}
