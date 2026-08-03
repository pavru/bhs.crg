using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;

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

    /// <summary>Предел глубины обхода — тот же, что у подмешивания: от патологических данных.</summary>
    private const int MaxDepth = 8;

    /// <summary>
    /// Ищет объекты-материалы с пустым полем документа качества.
    ///
    /// Обход идёт ПО СХЕМЕ документа, а не по всему контексту (issue #648), и вглубь — по всей
    /// цепочке составных полей. Почему по схеме: контекст, кроме реквизитов, содержит подмешанные
    /// общие данные и записи каталога, а «Наименование» с тэгом идентичности есть и у объекта
    /// строительства. Свободный обход выдал на живом документе предупреждение «Материал
    /// «комплексная застройка «ДНС Сити»…» без документа качества» — претензию к стройке. Схема
    /// отвечает на вопрос точно: материал — объект типа, СПОСОБНОГО нести документ качества
    /// (<see cref="MaterialIdentity.IsMaterial" />, тот же критерий, что у клиента).
    ///
    /// Глубина понадобилась потому, что материалы бывают не только массивом верхнего уровня: в АОСР
    /// они лежат внутри union-обёртки «массив ИЛИ ссылка на реестр» (#320). Иначе сертификат
    /// подставлялся бы, а сказать, что его нет, было бы некому — предупреждение молчало бы ровно
    /// там, где оно и нужно.
    ///
    /// Вглубь идут только ИНЛАЙН-составные поля (complex/array), но не ссылки (doc-ref/doc-array):
    /// правило «предупреждаем лишь о том, что человек видит на закладке и может привязать». Материалы
    /// чужой записи закладка показывает набором данных, а не разворотом ссылки (клиентский
    /// <c>collectMaterialRows</c> пропускает <c>$ref</c> по той же причине), и разворот ссылки здесь
    /// дал бы по предупреждению на каждую несопоставленную строку ЧУЖОГО реестра — в каждом
    /// документе, который на него ссылается, и без единой строки, к которой их приложить.
    ///
    /// Запускать ПОСЛЕ <see cref="IQualityLinkResolver.InjectAsync" />: до неё пусты вообще все, и
    /// предупреждение говорило бы о порядке шагов, а не о данных.
    /// </summary>
    public static void Scan(
        GenerationContext ctx,
        Guid documentTypeId,
        IReadOnlyList<DocumentType> allTypes,
        List<ResolutionDiagnostic> diagnostics)
    {
        var identityFields = MaterialIdentity.KeysOf(allTypes);
        var targetField = MaterialIdentity.QualityDocFieldOf(allTypes);
        if (identityFields.Length == 0 || string.IsNullOrEmpty(targetField)) return; // тэги не настроены

        var byId = allTypes.ToDictionary(t => t.Id);
        if (!byId.ContainsKey(documentTypeId)) return;

        // Схему одного типа разрешаем один раз на документ, а не на каждую строку: у реестра их 151,
        // и IsMaterial сам по себе обходит всю цепочку наследования.
        var schema = new SchemaMemo(byId, allTypes);

        foreach (var f in schema.InlineComposites(documentTypeId))
        {
            if (!ctx.Data.TryGetValue(f.Key, out var raw) || raw is not JsonElement value) continue;
            Walk(value, f.Key, f.TypeId!.Value, schema, identityFields, targetField, diagnostics, 0);
        }
    }

    private static void Walk(
        JsonElement value, string path, Guid typeId, SchemaMemo schema,
        string[] identityFields, string targetField,
        List<ResolutionDiagnostic> diagnostics, int depth)
    {
        if (depth >= MaxDepth) return;

        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = -1;
            foreach (var item in value.EnumerateArray())
                Walk(item, $"{path}[{++index}]", typeId, schema, identityFields, targetField, diagnostics, depth + 1);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) return;

        if (schema.IsMaterial(typeId))
        {
            Check(value, path, identityFields, targetField, diagnostics);
            return; // внутрь материала не идём: сертификат в его подполях искать нечего
        }

        // Обёртка (в том числе union): материалы могут лежать глубже.
        foreach (var inner in schema.InlineComposites(typeId))
        {
            if (!value.TryGetProperty(inner.Key, out var innerValue)) continue;
            Walk(innerValue, $"{path}.{inner.Key}", inner.TypeId!.Value, schema, identityFields, targetField, diagnostics, depth + 1);
        }
    }

    /// <summary>
    /// Ответы схемы, посчитанные один раз на тип: «этот тип — материал?» и «какие у него инлайн-
    /// составные поля». Оба зависят только от типа, а спрашивают их на каждый узел данных.
    /// </summary>
    private sealed class SchemaMemo(IReadOnlyDictionary<Guid, DocumentType> byId, IReadOnlyList<DocumentType> allTypes)
    {
        private readonly Dictionary<Guid, bool> _material = [];
        private readonly Dictionary<Guid, SchemaFieldInfo[]> _composites = [];

        public bool IsMaterial(Guid typeId)
        {
            if (_material.TryGetValue(typeId, out var known)) return known;
            var result = byId.TryGetValue(typeId, out var t) && MaterialIdentity.IsMaterial(t, allTypes);
            _material[typeId] = result;
            return result;
        }

        /// <summary>Поля-составные, лежащие В САМОМ документе: complex/array с известным типом.</summary>
        public SchemaFieldInfo[] InlineComposites(Guid typeId)
        {
            if (_composites.TryGetValue(typeId, out var known)) return known;
            var result = byId.ContainsKey(typeId)
                ? [.. DocumentTypeSchemaReader.EffectiveFields(typeId, byId)
                    .Where(f => f.Type is "complex" or "array" && f.TypeId is { } id && byId.ContainsKey(id))]
                : Array.Empty<SchemaFieldInfo>();
            _composites[typeId] = result;
            return result;
        }
    }

    private static void Check(
        JsonElement item, string path, string[] identityFields, string targetField,
        List<ResolutionDiagnostic> diagnostics)
    {
        // Материал ли это: у элемента есть хоть одно непустое поле идентичности. Тот же признак, по
        // которому строка получает ключ, — иначе предупреждение доставалось бы строкам, которые
        // сопоставлять и не собирались.
        var identity = IdentityKey.From(identityFields, f => Text(item, f));
        if (IdentityKey.IsEmpty(identity)) return;

        if (!MaterialIdentity.HasQualityDoc(item, targetField))
        {
            diagnostics.Add(new ResolutionDiagnostic(
                DiagnosticSeverity.Warning,
                $"{path}.{targetField}",
                $"Материал «{identity}» без документа качества: привязка не найдена.",
                Code));
            return;
        }

        // Документ есть — правдоподобен ли он (issue #586). Реквизиты документа уже подмешаны в
        // строку, отдельного запроса не нужно.
        if (!item.TryGetProperty(targetField, out var doc)) return;

        var docTexts = new List<string>();
        ProductScopeMatcher.CollectStrings(doc, docTexts);
        // Человеческие значения, а не ключ: сравнивать надо со словами, а ключ — машинный.
        var materialText = string.Join(' ', identityFields.Select(f => Text(item, f)).Where(v => !string.IsNullOrWhiteSpace(v)));

        if (ProductScopeMatcher.Assess(materialText, docTexts) == ProductScopeVerdict.Mismatched)
            diagnostics.Add(new ResolutionDiagnostic(
                DiagnosticSeverity.Warning,
                $"{path}.{targetField}",
                $"Материал «{identity}» не упоминается в области продукции привязанного документа — "
                + "возможно, сертификат не тот.",
                ImplausibleCode));
    }

    private static string? Text(JsonElement item, string field)
        => item.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

}
