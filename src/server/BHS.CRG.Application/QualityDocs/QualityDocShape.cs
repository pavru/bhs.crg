using System.Text.Json;
using BHS.CRG.Application.Generation;

namespace BHS.CRG.Application.QualityDocs;

/// <summary>
/// ЕДИНАЯ форма документа качества в контексте генерации (issue #736).
///
/// <para>Документ качества попадает в контекст двумя путями: развёрнутой instance-ссылкой
/// (<c>EntityResolver</c>, issue #733) и связью материал→документ (<c>QualityLinkResolver</c>,
/// issue #624). Формы разошлись: ссылка давала реквизиты плюс наименование, штамп типа и скан, а
/// связь — голые реквизиты. Шаблон, написанный на одну форму, молча давал пустоту на другой, и
/// узнать об этом автору было неоткуда: «сертификат» на месте, а <c>.Скан</c> существует только у
/// одного из двух путей. Отладка такого — худший вид: данные есть, поле пустое, диагностики нет.</para>
///
/// <para><b>Почему обогащённая, а не бедная.</b> Она строго богаче, и потерь при переходе нет. На
/// рабочей базе скан есть у ВСЕХ 32 документов качества, а связей материал→документ 54 — то есть
/// сведение к бедной форме отняло бы печать сертификата приложением к акту ровно там, где она
/// естественнее всего.</para>
///
/// <para><b>Чистая функция, а не общий резолвер.</b> Обогащение не требует ни базы, ни цепочки
/// областей — только сама запись. Разворот вложенных ссылок остаётся у того, кто это умеет:
/// <c>EntityResolver</c> подаёт сюда уже развёрнутые реквизиты, <c>QualityLinkResolver</c> — сырые
/// (их разберёт второй проход <c>ResolveContextRefsAsync</c>). Поэтому резолверы не начинают
/// зависеть друг от друга, а форма у них одна.</para>
///
/// <para><b>Что здесь НЕ выравнивается.</b> Общая только ОБОЛОЧКА — набор ключей и правило
/// перекрытия. Разрешение самих реквизитов у двух путей по-прежнему разное: ссылочный разворачивает
/// их с <c>allowInstanceRefs: false</c> (вложенная instance-ссылка станет стабом) и уже потратил
/// один шаг из предела глубины, а путь связи оставляет реквизиты второму проходу, где instance-ссылки
/// разворачиваются полностью. На сегодняшних данных это незаметно — instance-ссылок внутри реквизитов
/// документов качества нет, — но обещать «побайтово одинаково при любых реквизитах» было бы неправдой.
/// Выравнивать это значило бы решать, каким из двух поведений пожертвовать, а вопрос там не про
/// форму: он про то, до какой глубины вообще разворачивать документ качества.</para>
/// </summary>
public static class QualityDocShape
{
    /// <summary>
    /// Ключ, под которым скан попадает в объект (issue #733). Имя фиксировано намеренно: на него
    /// завязываются шаблоны, и менять его потом — ломать их все.
    /// </summary>
    public const string ScanKey = "Скан";

    /// <summary>Наименование самой записи библиотеки — его в реквизитах нет.</summary>
    public const string DisplayNameKey = "displayName";

    /// <summary>
    /// Собирает объект: реквизиты, затем служебные ключи.
    ///
    /// <para>Служебные кладутся ПОСЛЕ и перекрывают одноимённые: наименование и тип — свойства
    /// самой записи, а не её реквизитов, и авторитетна здесь запись. На рабочей базе таких
    /// реквизитов нет ни у одного из 32 документов, так что перекрытие сегодня теоретическое.</para>
    ///
    /// <para>Скан добавляется только когда он есть: у документа без скана ключа не будет, и
    /// одноимённый реквизит (если тип его объявляет) останется нетронутым — пустое вложение хуже
    /// его отсутствия, шаблон не отличил бы «скана нет» от «скан не загрузился».</para>
    /// </summary>
    /// <param name="requisites">Реквизиты — сырые или уже развёрнутые, на усмотрение вызывающего.</param>
    public static JsonElement Build(
        JsonElement requisites, string displayName, Guid documentTypeId,
        string? scanBlobPath, string? scanFileName, string? scanMimeType)
        => Build(EnumerateRequisites(requisites), displayName, documentTypeId,
            scanBlobPath, scanFileName, scanMimeType);

    /// <summary>
    /// То же, но реквизиты приходят уже разобранными по ключам — так их подаёт
    /// <c>EntityResolver</c>, который разворачивает каждое значение по отдельности.
    /// </summary>
    public static JsonElement Build(
        IEnumerable<KeyValuePair<string, JsonElement>> requisites, string displayName, Guid documentTypeId,
        string? scanBlobPath, string? scanFileName, string? scanMimeType)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in requisites) dict[key] = value;

        dict[DisplayNameKey] = JsonSerializer.SerializeToElement(displayName);
        dict[TypeStamper.TypeIdKey] = JsonSerializer.SerializeToElement(documentTypeId.ToString());

        if (!string.IsNullOrWhiteSpace(scanBlobPath))
            dict[ScanKey] = JsonSerializer.SerializeToElement(new Dictionary<string, string?>
            {
                ["$type"] = "file",
                ["blobPath"] = scanBlobPath,
                ["fileName"] = scanFileName,
                ["mimeType"] = scanMimeType,
            });

        return JsonSerializer.SerializeToElement(dict);
    }

    private static IEnumerable<KeyValuePair<string, JsonElement>> EnumerateRequisites(JsonElement requisites)
    {
        if (requisites.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in requisites.EnumerateObject())
            yield return new(p.Name, p.Value.Clone());
    }
}
