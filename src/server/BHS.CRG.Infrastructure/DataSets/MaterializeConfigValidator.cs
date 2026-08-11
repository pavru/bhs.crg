using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Проверка настройки материализации перед сохранением (issue #716).
///
/// Зачем она вообще. Дискриминатор — единственное место, где маппинг перестаёт быть «поле → колонка»
/// и становится правилом, применяемым к каждой строке отдельно. Противоречивое правило не падает при
/// сохранении и не видно в диалоге: оно проявляется молчаливо пропущенными строками через месяц, в
/// готовом реестре, где недостача не бросается в глаза. Поэтому отказ здесь — на сохранении, словами
/// и до того, как настройка начнёт работать.
///
/// Проверяется настройка ЦЕЛИКОМ (маппинг вместе с правилами): по отдельности они бессмысленны —
/// правило варианта без маппинга не заполнит ничего, а несколько ключей маппинга без правил некому
/// разложить по строкам.
/// </summary>
public static class MaterializeConfigValidator
{
    public static void Validate(
        DocumentType type,
        IReadOnlyDictionary<string, string> mapping,
        MaterializeDiscriminatorConfig? discriminator,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        string? byIdColumn = null)
    {
        var isUnion = SchemaTags.TypeHasTag(type, [.. typesById.Values], FunctionalTag.TypeUnion);

        // Режим «существующий документ по Ид» (issue #725): вся строка — ссылка на документ.
        if (!string.IsNullOrWhiteSpace(byIdColumn))
        {
            // Ссылаться можно только на экземпляр документа: у составного типа их не бывает, и
            // ссылка на «строку реестра» не значила бы ничего.
            if (type.Kind != DocumentTypeKind.Document)
                throw new InvalidRequestException(
                    $"Тип «{type.Name}» не является типом документа — ссылаться на существующий документ по Ид для него нельзя.");

            // Маппинг и режим «по Ид» — два разных ответа на вопрос «что такое строка». Сохранить оба
            // значит оставить настройку, в которой видимое в диалоге не совпадает с результатом.
            if (mapping.Any(p => !string.IsNullOrEmpty(p.Value)))
                throw new InvalidRequestException(
                    "В режиме «существующий документ по Ид» строка целиком становится ссылкой — маппинг колонок в нём не задаётся.");

            // Дискриминатор — про выбор варианта union'а, то есть про составной тип; сюда он попасть
            // не может, но отказ дешевле молчаливого игнорирования настройки.
            if (discriminator is not null)
                throw new InvalidRequestException(
                    "Вариант по типу документа строки задаётся только при сборке объекта из колонок.");

            return;
        }

        // Дискриминатор осмыслен только для union'а: у обычного типа строка заполняет все свои поля
        // сразу, выбирать не из чего.
        if (discriminator is not null && !isUnion)
            throw new InvalidRequestException(
                $"Тип «{type.Name}» не является union'ом — вариант по типу документа строки для него не задаётся.");

        if (!isUnion) return;

        var variantKeys = DocumentTypeSchemaReader.EffectiveFields(type.Id, typesById)
            .Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var key in mapping.Keys)
            if (!variantKeys.Contains(key))
                throw new InvalidRequestException(
                    $"Вариант «{key}» не объявлен у типа «{type.Name}».");

        if (discriminator is null)
        {
            // Без правила разложить строки по вариантам НЕЧЕМ, а union по определению заполняется
            // одним вариантом. Несколько ключей здесь дали бы экземпляр, в котором заполнено всё
            // сразу, — то есть не union.
            if (mapping.Count > 1)
                throw new InvalidRequestException(
                    "Union заполняется одним вариантом. Чтобы разложить строки по вариантам, " +
                    "задайте вариант по типу документа строки.");
            return;
        }

        if (string.IsNullOrWhiteSpace(discriminator.Column))
            throw new InvalidRequestException("Не выбрана колонка, по которой определяется вариант.");

        var ruleTypeOwners = new Dictionary<Guid, string>();
        foreach (var (variantKey, ruleTypeIds) in discriminator.Rules)
        {
            if (!variantKeys.Contains(variantKey))
                throw new InvalidRequestException(
                    $"Правило задано для варианта «{variantKey}», которого у типа «{type.Name}» нет.");

            // Вариант с правилом, но без маппинга, заполнить нечем: строки его типов дали бы
            // пустой объект — ровно ту молчаливую пустоту, ради которой заведён issue #715.
            if (ruleTypeIds.Count > 0 && !mapping.ContainsKey(variantKey))
                throw new InvalidRequestException(
                    $"Для варианта «{variantKey}» назначены типы документов, но не задан маппинг колонок.");

            foreach (var typeId in ruleTypeIds)
            {
                // Один тип у двух вариантов — противоречие, а не мелочь: «кто первый» здесь решал бы
                // порядок ключей в JSON, которого никто не задумывал.
                if (ruleTypeOwners.TryGetValue(typeId, out var other) && other != variantKey)
                {
                    var name = typesById.TryGetValue(typeId, out var t) ? t.Name : typeId.ToString();
                    throw new InvalidRequestException(
                        $"Тип документа «{name}» назначен сразу двум вариантам — «{other}» и «{variantKey}».");
                }
                ruleTypeOwners[typeId] = variantKey;
            }
        }
    }
}
