using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>Почему строка не материализовалась. Один словарь на генерацию и предпросмотр —
/// иначе два экрана объясняли бы одно и то же разными словами.</summary>
public static class MaterializeSkipReason
{
    public const string EmptyValue = "discriminator-empty";
    public const string UnknownTypeCode = "unknown-type-code";
    public const string DocumentNotFound = "document-not-found";
    public const string NoVariant = "no-variant";
    public const string Ambiguous = "ambiguous-variant";
    public const string VariantNotMapped = "variant-not-mapped";
    /// <summary>Режим «существующий документ по Ид» (issue #725): колонка с Ид пуста / в ней не Ид.</summary>
    public const string RefIdEmpty = "ref-id-empty";
    public const string RefIdNotGuid = "ref-id-not-guid";

    public static string Describe(string code) => code switch
    {
        EmptyValue => "колонка-признак пуста",
        UnknownTypeCode => "код типа документа не найден",
        DocumentNotFound => "документ по идентификатору не найден",
        NoVariant => "для типа документа не назначен вариант",
        Ambiguous => "тип документа назначен двум вариантам одинаково точно",
        VariantNotMapped => "у выбранного варианта не задан маппинг колонок",
        RefIdEmpty => "колонка с идентификатором документа пуста",
        RefIdNotGuid => "в колонке не идентификатор документа",
        _ => code,
    };
}

/// <summary>Выбор варианта для одной строки: ключ варианта либо причина пропуска.</summary>
public record VariantChoice(string? VariantKey, string? SkipReason)
{
    public static readonly VariantChoice None = new(null, null);
}

/// <summary>
/// Выбирает вариант union'а для каждой строки по дискриминатору (issue #716).
///
/// <para><b>Специфичность решает наследование.</b> Правило варианта называет тип, и подходит любой
/// его потомок: назначив варианту «АОСР» сам тип АОСР, получаешь и все его подвиды. Когда строка
/// подходит двум вариантам, выигрывает тот, чей тип БЛИЖЕ по цепочке наследования — иначе общий
/// предок в одном варианте перебивал бы точное совпадение в другом. Ничья (одинаковая близость у
/// двух вариантов) — не повод угадывать: настройка противоречива, и строка пропускается с ошибкой.
/// «Первый по порядку» тут был бы худшим решением: порядок ключей в JSON никто не задумывал.</para>
///
/// <para>Резолв типа по идентификатору документа НЕ делается построчно: все идентификаторы страницы
/// разрешаются одним запросом до цикла (см. <see cref="DocumentIdsIn" />) — иначе реестр на сотню
/// строк давал бы сотню запросов.</para>
/// </summary>
public sealed class MaterializeVariantSelector
{
    private readonly MaterializeDiscriminatorConfig _config;
    private readonly IReadOnlyDictionary<Guid, DocumentType> _typesById;
    private readonly IReadOnlyDictionary<string, Guid> _typeIdByCode;
    private readonly IReadOnlyDictionary<Guid, Guid> _typeIdByDocumentId;

    private MaterializeVariantSelector(
        MaterializeDiscriminatorConfig config,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        IReadOnlyDictionary<Guid, Guid> typeIdByDocumentId)
    {
        _config = config;
        _typesById = typesById;
        _typeIdByDocumentId = typeIdByDocumentId;
        _typeIdByCode = typesById.Values
            .Where(t => !string.IsNullOrWhiteSpace(t.Code))
            .GroupBy(t => t.Code!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Разбирает конфигурацию; null — не задана либо негодна (тогда материализация статична).</summary>
    public static MaterializeDiscriminatorConfig? ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<MaterializeDiscriminatorConfig>(json, JsonOptions);
            return parsed is null || string.IsNullOrWhiteSpace(parsed.Column) ? null : parsed;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Идентификаторы документов, встреченные в колонке-признаке, — для ОДНОГО запроса типов до
    /// построчного цикла. Пусто, если признак читается кодом типа.
    /// </summary>
    public static IReadOnlyCollection<Guid> DocumentIdsIn(
        MaterializeDiscriminatorConfig config, IEnumerable<IReadOnlyDictionary<string, string?>> rows)
    {
        if (!config.IsByDocumentId) return [];
        var ids = new HashSet<Guid>();
        foreach (var row in rows)
            if (row.TryGetValue(config.Column, out var cell) && Guid.TryParse(cell?.Trim(), out var id))
                ids.Add(id);
        return ids;
    }

    public static MaterializeVariantSelector Create(
        MaterializeDiscriminatorConfig config,
        IReadOnlyDictionary<Guid, DocumentType> typesById,
        IReadOnlyDictionary<Guid, Guid>? typeIdByDocumentId = null)
        => new(config, typesById, typeIdByDocumentId ?? new Dictionary<Guid, Guid>());

    public VariantChoice Choose(IReadOnlyDictionary<string, string?> row)
    {
        var raw = row.TryGetValue(_config.Column, out var cell) ? cell?.Trim() : null;
        if (string.IsNullOrEmpty(raw)) return new VariantChoice(null, MaterializeSkipReason.EmptyValue);

        Guid rowTypeId;
        if (_config.IsByDocumentId)
        {
            if (!Guid.TryParse(raw, out var documentId) || !_typeIdByDocumentId.TryGetValue(documentId, out rowTypeId))
                return new VariantChoice(null, MaterializeSkipReason.DocumentNotFound);
        }
        else if (!_typeIdByCode.TryGetValue(raw, out rowTypeId))
        {
            return new VariantChoice(null, MaterializeSkipReason.UnknownTypeCode);
        }

        string? best = null;
        var bestDistance = int.MaxValue;
        var tied = false;

        foreach (var (variantKey, ruleTypeIds) in _config.Rules)
        {
            foreach (var ruleTypeId in ruleTypeIds)
            {
                var distance = InheritanceDistance(rowTypeId, ruleTypeId);
                if (distance is null) continue;
                if (distance < bestDistance)
                {
                    best = variantKey;
                    bestDistance = distance.Value;
                    tied = false;
                }
                else if (distance == bestDistance && best is not null && best != variantKey)
                {
                    tied = true;
                }
            }
        }

        if (best is null) return new VariantChoice(null, MaterializeSkipReason.NoVariant);
        return tied
            ? new VariantChoice(null, MaterializeSkipReason.Ambiguous)
            : new VariantChoice(best, null);
    }

    /// <summary>
    /// Сколько шагов наследования от типа строки вверх до типа из правила; null — не потомок.
    /// Ноль — точное совпадение.
    /// </summary>
    private int? InheritanceDistance(Guid rowTypeId, Guid ruleTypeId)
    {
        var current = rowTypeId;
        // Ограничение шагов — против цикла в данных: цепочка типов строится пользователем, и
        // испорченный parentId не должен вешать генерацию.
        for (var step = 0; step < 32; step++)
        {
            if (current == ruleTypeId) return step;
            if (!_typesById.TryGetValue(current, out var type) || type.ParentId is not { } parent) return null;
            current = parent;
        }
        return null;
    }
}
