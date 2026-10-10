using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Application.Documents;

/// <summary>Запись на выбор в поле-ссылке: единица измерения и подобные справочники.</summary>
public sealed record IntakeOption(Guid Id, string? Name);

/// <summary>
/// Поле, которое спрашивает окно «Новая позиция номенклатуры».
/// </summary>
/// <param name="Identity">Поле входит в ключ идентичности: по нему позицию сверяют с лежащими.</param>
/// <param name="TargetTypeId">Не <c>null</c> — поле заполняется ВЫБОРОМ записи этого типа, а не
/// текстом.</param>
/// <param name="Options">Записи на выбор; у текстового поля пусто. Заполняет служба.</param>
public sealed record IntakeField(
    string Key, string Title, bool Required, bool Identity, Guid? TargetTypeId,
    IReadOnlyList<IntakeOption> Options);

/// <summary>
/// Вид позиции — «Номенклатура» или её подтип — и можно ли завести его коротким окном.
/// </summary>
/// <param name="Refusals">Почему нельзя — словами для человека. Пусто — можно.</param>
public sealed record IntakeKind(
    Guid TypeId, string Code, string Name, IReadOnlyList<IntakeField> Fields, IReadOnlyList<string> Refusals);

/// <summary>Что завести: вид, тексты по ключам полей и выбранные записи по ключам полей-ссылок.</summary>
public sealed record NomenclatureIntakeRequest(
    Guid TypeId, IReadOnlyDictionary<string, string?> Values, IReadOnlyDictionary<string, Guid> Refs);

/// <summary>Заполнено одно из двух: заведённая позиция либо та, что уже лежит с тем же ключом.</summary>
public sealed record NomenclatureIntakeOutcome(DomainObject? Created, SimilarRecord? Existing);

/// <summary>
/// Создание позиции номенклатуры коротким окном и поиск похожих (задача C3, issue #1079, ТЗ COST-7.1,
/// TYPE-8) — дверь ЯДРА под <c>core.nomenclature.edit</c>.
///
/// <para>Служба, а не команда — по той же причине, что <see cref="ICatalogIntake" />: проверка «такой
/// ещё нет» и создание идут под одним замком, а транзакцию открывает владелец контекста базы.</para>
/// </summary>
public interface INomenclatureIntake
{
    /// <summary>Виды позиций и что о каждом спросить. Типа «Номенклатура» нет — отказ с причиной.</summary>
    Task<IReadOnlyList<IntakeKind>> DescribeAsync(CancellationToken ct = default);

    /// <summary>Лежащие позиции, похожие на набранное. Создания не делает.</summary>
    Task<SimilarAnswer> SimilarAsync(
        Guid typeId, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default);

    Task<NomenclatureIntakeOutcome> CreateAsync(NomenclatureIntakeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Что спросить о новой позиции и как сложить ответ в данные записи — чистые функции над СХЕМОЙ.
///
/// <para><b>Ключей полей здесь нет ни одного.</b> Тип «Номенклатура» заводит не код, а миграция — из
/// «Материала» заказчика, как он лежал: состав полей у каждой установки свой. Поэтому окно спрашивает
/// то, что называет схема: поля ключа идентичности и обязательные поля, которые можно заполнить
/// текстом или выбором записи. Впиши мы сюда «Наименование» и «Артикул» — дверь работала бы только
/// там, где администратор их не переименовал.</para>
///
/// <para>⚠️ <b>Обязательное поле, которое окну заполнить нечем, — отказ, а не пропуск.</b> Охрана
/// записи обязательность не проверяет (это правило перехода), и без отказа легла бы запись, которую
/// её собственная форма сохранить не даст. Так у «Материала»: обязательное «Количество» — свойство
/// строки, и спрашивать его у справочника нелепо.</para>
/// </summary>
public static class NomenclatureIntakeLayout
{
    /// <summary>Справочник в поле-ссылке длиннее этого — выбором в коротком окне его не заполнить.</summary>
    public const int OptionsLimit = 200;

    public static IntakeKind Describe(DocumentType type, IReadOnlyDictionary<Guid, DocumentType> types)
    {
        var refusals = new List<string>();
        if (!TypeStorageRules.KeptInCommonTable(type))
            refusals.Add($"записи вида «{type.Name}» хранит модуль «{type.Module}» — общим справочником они не заводятся");

        var all = types.Values.ToList();
        var identity = SchemaTags.OrderedKeysWithTag(type, all, FunctionalTag.Identity);
        var schema = DocumentTypeSchemaReader.EffectiveFields(type.Id, types)
            .Where(f => !f.Computed).ToList();

        var asked = new List<IntakeField>();
        var unfillable = new List<string>();

        // Сначала ключ идентичности — в порядке ключа: первым идёт то, чем позицию называют.
        foreach (var key in identity)
        {
            if (schema.FirstOrDefault(f => f.Key == key) is not { } field) continue;
            if (IsText(field.Type) && !field.Locked)
                asked.Add(new(field.Key, Title(field), field.Required, Identity: true, null, []));
            else
                refusals.Add($"поле ключа «{Title(field)}» — не строка, которую заполняет человек: " +
                    "сверить новую позицию с лежащими нечем");
        }

        if (!asked.Any(f => f.Identity) && refusals.Count == 0)
            refusals.Add("в схеме вида нет поля с тэгом «Идентификатор»: назвать позицию и сверить её с лежащими нечем");

        foreach (var field in schema.Where(f => f.Required && !identity.Contains(f.Key)))
        {
            if (field.Locked) unfillable.Add(Title(field));
            else if (IsText(field.Type))
                asked.Add(new(field.Key, Title(field), true, Identity: false, null, []));
            else if (field is { Type: "complex", TypeId: { } target }
                     && types.TryGetValue(target, out var targetType)
                     && TypeStorageRules.KeptInCommonTable(targetType))
                asked.Add(new(field.Key, Title(field), true, Identity: false, target, []));
            else unfillable.Add(Title(field));
        }

        if (unfillable.Count > 0)
            refusals.Add("обязательны поля, которые отсюда не заполнить: "
                + string.Join(", ", unfillable.Select(t => $"«{t}»")));

        return new(type.Id, type.Code ?? "", type.Name, asked, refusals);
    }

    /// <summary>
    /// Данные новой записи и её название. Отказ — <see cref="InvalidRequestException" />: причина в
    /// запросе, и человек чинит её в том же окне.
    /// </summary>
    /// <param name="refs">Выбранные записи с названиями — уже проверенные службой.</param>
    public static (JsonDocument Data, string Name) Build(
        IntakeKind kind, IReadOnlyDictionary<string, string?> values, IReadOnlyDictionary<string, IntakeOption> refs)
    {
        if (kind.Refusals.Count > 0)
            throw new InvalidRequestException(
                $"Позицию вида «{kind.Name}» отсюда не завести: {string.Join("; ", kind.Refusals)}.");

        // Лишний ключ — отказ, а не молчаливый пропуск: окно, приславшее поле мимо описания, устарело
        // либо ошиблось, и «завелось без него» человек не заметит.
        var known = kind.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var foreign = values.Keys.Concat(refs.Keys).Where(k => !known.Contains(k)).Distinct().ToList();
        if (foreign.Count > 0)
            throw new InvalidRequestException(
                $"Вид «{kind.Name}» не спрашивает: {string.Join(", ", foreign.Select(k => $"«{k}»"))}.");

        var data = new JsonObject();
        var missing = new List<string>();
        foreach (var field in kind.Fields)
        {
            if (field.TargetTypeId is not null)
            {
                if (refs.TryGetValue(field.Key, out var picked))
                    data[field.Key] = new JsonObject
                    {
                        ["$ref"] = "catalog", ["entryId"] = picked.Id.ToString(), ["displayName"] = picked.Name,
                    };
                else if (field.Required) missing.Add(field.Title);
                continue;
            }

            var text = values.TryGetValue(field.Key, out var typed) ? Clean(typed) : "";
            if (text.Length > 0) data[field.Key] = text;
            else if (field.Required) missing.Add(field.Title);
        }

        // Название — первое поле ключа. Оно обязано быть, даже если схема его обязательным не зовёт:
        // запись без названия в выборе не найти, а завели её ровно затем, чтобы выбрать.
        var naming = kind.Fields.First(f => f.Identity);
        var name = values.TryGetValue(naming.Key, out var said) ? Clean(said) : "";
        if (name.Length == 0 && !missing.Contains(naming.Title)) missing.Insert(0, naming.Title);

        if (missing.Count > 0)
            throw new InvalidRequestException(
                "Не заполнено: " + string.Join(", ", missing.Select(t => $"«{t}»")) + ".");

        return (JsonSerializer.SerializeToDocument(data), name);
    }

    /// <summary>Пробелы по краям и подряд — оформление, а не значение: в справочник ложится чистое.</summary>
    public static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsText(string type) => type is "string" or "text";

    private static string Title(SchemaFieldInfo field) => string.IsNullOrWhiteSpace(field.Title) ? field.Key : field.Title;
}
