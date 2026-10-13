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
    private static readonly FrozenDictionary<BrandIconKind, (byte[] Content, string MimeType)> Files =
        new Dictionary<BrandIconKind, (string File, string MimeType)>
        {
            [BrandIconKind.Tab] = ("tab.svg", "image/svg+xml"),
            [BrandIconKind.Apple] = ("apple.png", "image/png"),
            [BrandIconKind.App192] = ("app-192.png", "image/png"),
            [BrandIconKind.App512] = ("app-512.png", "image/png"),
            [BrandIconKind.Maskable512] = ("maskable-512.png", "image/png"),
        }.ToFrozenDictionary(p => p.Key, p => (Read(p.Value.File), p.Value.MimeType));

    /// <summary>
    /// Метка версии стандартных значков — по содержимому всех пяти, а не постоянное слово. Адрес с
    /// действующей меткой получает длинный кеш; с постоянной меткой сборка с новыми картинками
    /// (ребрендинг, как в #728) отдавала бы их под прежним адресом, и браузер неделю показывал бы
    /// старые, не спрашивая сервер. Ручной метки, которую поднимали в index.html и в манифесте,
    /// больше нет — и прятаться ей в константе незачем.
    /// </summary>
    public static readonly string Version = "std-" + Hex(SHA256.HashData(
        Enum.GetValues<BrandIconKind>().SelectMany(kind => Files[kind].Content).ToArray()), 8);

    private static readonly FrozenDictionary<BrandIconKind, BrandIcon> Icons = Files.ToFrozenDictionary(
        p => p.Key,
        // Тег — по содержимому самой картинки: адрес без метки переспрашивается одним 304.
        p => new BrandIcon(p.Value.Content, p.Value.MimeType,
            $"\"std-{Hex(SHA256.HashData(p.Value.Content), 16)}\"", Version));

    public static BrandIcon Get(BrandIconKind kind) => Icons[kind];

    private static byte[] Read(string file)
    {
        // Имя ресурса задано в csproj (LogicalName), а не выведено из папок: вывод зависит от
        // пространства имён проекта и молча меняется при переносе файла.
        using var stream = typeof(StandardBrandIcons).Assembly.GetManifestResourceStream("brand-icons/" + file)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Hex(byte[] hash, int length) => Convert.ToHexStringLower(hash)[..length];
}
