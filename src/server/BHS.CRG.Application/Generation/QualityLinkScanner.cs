using System.Text.Json;
using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Проверки связки материала с документом качества: документа НЕТ (issue #585) и документ есть, но
/// не про этот материал (issue #586).
///
/// Составной ключ (#582) строже прежнего «совпало любое поле», поэтому несопоставленных материалов
/// станет больше ПО ПОСТРОЕНИЮ — и это не дефект, а перенос решения на пользователя: одна позиция,
/// записанная в разных таблицах по-разному, даёт разные ключи, и только он решает — исправить
/// материал или завести вторую связку. Но решать он может лишь то, что видит.
///
/// Уровень — Warning, а не Error: на замере комплекта 250701.ЭОМ-1 из 151 строки реестра 75 не имели
/// сертификата вовсе, и выпуск документа блокировать нельзя — иначе система встанет в тот же день.
/// Молчать тоже нельзя: сегодня такой документ выпускается без единого следа в UI.
/// </summary>
public static class QualityLinkScanner
{
    public const string Code = "material-no-quality-doc";

    /// <summary>Документ есть, но материал не упоминается в его области продукции (issue #586).</summary>
    public const string ImplausibleCode = "quality-doc-implausible";

    /// <summary>
    /// Ищет в массивах контекста элементы-материалы (опознаются полями идентичности — тем же
    /// способом, что и сопоставление) с пустым полем документа качества.
    ///
    /// Запускать ПОСЛЕ <see cref="IQualityLinkResolver.InjectAsync" />: до неё пусты вообще все, и
    /// предупреждение говорило бы о порядке шагов, а не о данных.
    /// </summary>
    public static void Scan(
        GenerationContext ctx,
        IReadOnlyList<string> identityFields,
        string? targetField,
        List<ResolutionDiagnostic> diagnostics)
    {
        if (identityFields.Count == 0 || string.IsNullOrEmpty(targetField)) return; // тэги не настроены

        foreach (var (key, value) in ctx.Data)
        {
            if (value is not JsonElement el || el.ValueKind != JsonValueKind.Array) continue;

            var index = -1;
            foreach (var item in el.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object) continue;

                // Материал ли это: у элемента есть хоть одно непустое поле идентичности. Тот же
                // признак, по которому строка получает ключ, — иначе предупреждение доставалось бы
                // строкам, которые сопоставлять и не собирались.
                var identity = IdentityKey.From(identityFields, f => Text(item, f));
                if (IdentityKey.IsEmpty(identity)) continue;

                if (!HasValue(item, targetField))
                {
                    diagnostics.Add(new ResolutionDiagnostic(
                        DiagnosticSeverity.Warning,
                        $"{key}[{index}].{targetField}",
                        $"Материал «{identity}» без документа качества: привязка не найдена.",
                        Code));
                    continue;
                }

                // Документ есть — правдоподобен ли он (issue #586). Реквизиты документа уже
                // подмешаны в строку, отдельного запроса не нужно.
                if (!item.TryGetProperty(targetField, out var doc)) continue;

                var docTexts = new List<string>();
                ProductScopeMatcher.CollectStrings(doc, docTexts);
                // Человеческие значения, а не ключ: сравнивать надо со словами, а ключ — машинный.
                var materialText = string.Join(' ', identityFields.Select(f => Text(item, f)).Where(v => !string.IsNullOrWhiteSpace(v)));

                if (ProductScopeMatcher.Assess(materialText, docTexts) == ProductScopeVerdict.Mismatched)
                    diagnostics.Add(new ResolutionDiagnostic(
                        DiagnosticSeverity.Warning,
                        $"{key}[{index}].{targetField}",
                        $"Материал «{identity}» не упоминается в области продукции привязанного документа — "
                        + "возможно, сертификат не тот.",
                        ImplausibleCode));
            }
        }
    }

    private static string? Text(JsonElement item, string field)
        => item.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Заполнено ли целевое поле — тот же предикат, что у резолвера: он не перетирает
    /// заданное вручную, и предупреждать о заполненном вручную было бы ложной тревогой.</summary>
    private static bool HasValue(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(v.GetString()),
            JsonValueKind.Object => v.EnumerateObject().Any(),
            JsonValueKind.Array => v.GetArrayLength() > 0,
            _ => true,
        };
    }
}
