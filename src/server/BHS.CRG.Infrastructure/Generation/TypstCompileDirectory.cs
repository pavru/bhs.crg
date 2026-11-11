using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>Форма <c>data.json</c>: Typst значения форматирования не видит, человек — видит.</summary>
public enum JsonLayout
{
    /// <summary>Генерация: компактно.</summary>
    Compact,
    /// <summary>Отладочный пакет: с отступами и нескрытой кириллицей — его читают глазами.</summary>
    Indented,
}

/// <summary>Что записанный каталог говорит компиляции.</summary>
/// <param name="FontsDir">Каталог шрифтов для <c>--font-path</c>; есть, только если записан хоть один шрифт.</param>
/// <param name="UnreadableImages">Адреса полей, чью картинку хранилище не отдало (ТЗ CORE-37.2).</param>
/// <param name="InvalidAssetNames">Ассеты-картинки шаблона, чьё имя не простое имя файла
/// (<see cref="TemplateAsset.IsValidImageName" />); они не записаны (change core-template-asset-names, D3).</param>
public sealed record CompileLayout(
    string? FontsDir, IReadOnlyList<string> UnreadableImages, IReadOnlyList<string> InvalidAssetNames);

/// <summary>
/// Каталог компиляции Typst — ОДНА запись на генерацию и отладочный пакет (ТЗ CORE-21, CORE-22; change
/// core-generation-input-one-path, D3). Пакет архивирует ровно то, что записано здесь, поэтому
/// «пакет воспроизводит вход генерации» держится на построении, а не на внимательности: прежде пакет
/// писал вход своей копией, и копия разошлась — без предела размера ассетов, с проглоченной отменой,
/// с подсказкой <c>--font-path</c> там, где генерация ключа не передаёт.
///
/// <code>
/// &lt;dir&gt;/data.json, template.typ, systemlib.typ   — данные (картинки и вложения материализованы), шаблон дословно
/// &lt;dir&gt;/typeblocks.typ, typeblocks/…            — блоки типов (TypeBlocksMaterializer)
/// &lt;dir&gt;/userlib.typ, userlib/…                  — библиотека (UserLibMaterializer)
/// &lt;dir&gt;/fields/img_N.ext, att_N.ext              — картинки полей, вложения
/// &lt;dir&gt;/assets/&lt;ассет&gt;.ext                      — ассеты-картинки шаблона, и только они
/// &lt;dir&gt;/fonts/font_N.ext                          — шрифты шаблона (для --font-path)
/// </code>
/// </summary>
public sealed class TypstCompileDirectory(IBlobStorage blob)
{
    /// <summary>Предел одного файла — и для вложений, и для ассетов шаблона.</summary>
    public const long MaxFileBytes = TypstFileMaterializer.MaxAttachmentBytes;

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<CompileLayout> WriteAsync(
        GenerationRequest request, string dir, JsonLayout layout, CancellationToken ct)
    {
        // Поля-изображения (data-URI и ссылки на блоб) — в файлы fields/, в данных — путь и размер
        // (issue #246, #522). Непрочитанная картинка остаётся ссылкой, её поле — в UnreadableImages.
        // Каталог полей отделён от assets/: ассет шаблона по имени до них не дотягивается (D1).
        var unreadable = new List<string>();
        var dataJson = await TypstImageMaterializer.MaterializeAsync(
            request.Context.Data, dir, blob, TypstGenerator.FieldsSubdir, unreadable: unreadable, ct: ct);

        // Поля-вложения ({$type:"file"}) — в fields/att_N, в данных — путь и pageCount, чтобы шаблон
        // вставлял их через image(it.Поле.src[, page: n]) — в т.ч. страницы PDF.
        var node = JsonNode.Parse(dataJson) ?? new JsonObject();
        var fieldsDir = Path.Combine(dir, TypstGenerator.FieldsSubdir);
        var attCount = 0;
        await new TypstFileMaterializer(blob).MaterializeAsync(node, (bytes, ext) =>
        {
            Directory.CreateDirectory(fieldsDir);
            var name = $"att_{attCount++}.{ext}";
            File.WriteAllBytes(Path.Combine(fieldsDir, name), bytes);
            return AssetPath.FromRoot(TypstGenerator.FieldsSubdir, name);
        }, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, TypstGenerator.DataFileName),
            layout == JsonLayout.Indented ? node.ToJsonString(IndentedJson) : node.ToJsonString(), ct);

        // template.typ — ДОСЛОВНО (issue #353): стандартные импорты живут в самом шаблоне, поэтому
        // номера строк ошибок = строки редактора.
        await File.WriteAllTextAsync(Path.Combine(dir, "template.typ"), request.TemplateContent, Encoding.UTF8, ct);
        // systemlib.typ — всегда, чтобы `#import "systemlib.typ"` резолвился (issue #344).
        await File.WriteAllTextAsync(Path.Combine(dir, SystemTypstLib.FileName), SystemTypstLib.Content, Encoding.UTF8, ct);

        // Блоки типов: агрегатор пишется ВСЕГДА, хотя бы заглушкой — без него упал бы каждый документ.
        await TypeBlocksMaterializer.WriteAsync(dir, request.TypeBlocksFiles, ct);
        if (!File.Exists(Path.Combine(dir, TypstGenerator.TypeBlocksFileName)))
            throw new InvalidOperationException($"Failed to write {TypstGenerator.TypeBlocksFileName} to {dir}");

        // Точка входа библиотеки — всегда; рядом дерево userlib/ (issue #473).
        await UserLibMaterializer.WriteAsync(dir, request.UserLibContent, request.UserLibFiles, ct);

        var fontsDir = await WriteTemplateAssetsAsync(request, dir, Path.Combine(dir, TypstGenerator.AssetsSubdir), ct);
        return new CompileLayout(fontsDir, unreadable, InvalidAssetNames(request));
    }

    /// <summary>
    /// Ассеты-картинки запроса с именем не по правилу (<see cref="TemplateAsset.IsValidImageName" />) — их
    /// каталог не пишет (D3). Одно место на обоих: генерация отказывает по нему ещё до записи каталога,
    /// отладочный пакет называет их в README.
    /// </summary>
    public static IReadOnlyList<string> InvalidAssetNames(GenerationRequest request)
        => request.TemplateAssets is { } assets
            ? [.. assets.Images.Where(i => !TemplateAsset.IsValidImageName(i.Name)).Select(i => i.Name)]
            : [];

    /// <summary>
    /// Ассеты шаблона (issue #62), уже свёрнутые резолвером по приоритету. Картинки — в assets/ по
    /// стабильному Name (шаблон зовёт <c>image("/assets/{Name}.{ext}")</c>); шрифты — в fonts/: Typst
    /// находит шрифт по семейству, зашитому в файл, а не по имени файла.
    /// <para>Картинка с именем не по правилу (из старых данных или копии — новое такое не создать) не
    /// скачивается и не пишется (<see cref="InvalidAssetNames" />): её Name адресовал бы не тот файл,
    /// что зовёт шаблон (D3).</para>
    /// </summary>
    private async Task<string?> WriteTemplateAssetsAsync(GenerationRequest request, string dir,
        string assetsDir, CancellationToken ct)
    {
        if (request.TemplateAssets is not { } assets) return null;

        foreach (var img in assets.Images)
        {
            if (!TemplateAsset.IsValidImageName(img.Name)) continue;
            var bytes = await TypstFileMaterializer.TryDownloadAsync(blob, img.BlobPath, ct);
            if (bytes is null) continue;
            Directory.CreateDirectory(assetsDir);
            await File.WriteAllBytesAsync(
                Path.Combine(assetsDir, $"{img.Name}{Path.GetExtension(img.FileName)}"), bytes, ct);
        }

        // Каталог шрифтов — только под записанный шрифт: пустой каталог ничего не меняет в компиляции,
        // а в пакете стал бы подсказкой «компилируйте с --font-path», которой генерация не следует.
        string? fontsDir = null;
        var fontIndex = 0;
        foreach (var font in assets.Fonts)
        {
            var bytes = await TypstFileMaterializer.TryDownloadAsync(blob, font.BlobPath, ct);
            if (bytes is null) continue;
            fontsDir ??= Directory.CreateDirectory(Path.Combine(dir, TypstGenerator.FontsSubdir)).FullName;
            await File.WriteAllBytesAsync(
                Path.Combine(fontsDir, $"font_{fontIndex++}{Path.GetExtension(font.FileName)}"), bytes, ct);
        }
        return fontsDir;
    }
}
