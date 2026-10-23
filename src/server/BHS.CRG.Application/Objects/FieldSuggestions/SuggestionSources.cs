using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Application.Objects.FieldSuggestions;

/// <summary>
/// Что прочитать подсказке поля — или почему читать нечего (issue #1530, эпик #1527, D1, D5).
///
/// <para>Ответ — один из четырёх, и различать их обязан вызывающий: «нет такого поля» — 400, «поле не
/// подсказывается» и «модуль выключен» — 200 и пусто, «нет прав» — 403. Сведи их к пустому списку —
/// и поле, о котором ошиблись адресом, молчало бы так же, как поле без введённых значений.</para>
/// </summary>
public abstract record SuggestionPlan
{
    /// <summary>Тип или поле не существуют: запрос ошибочен (400).</summary>
    public sealed record Unknown(string Reason) : SuggestionPlan;

    /// <summary>Читать нечего по устройству — 200 и пусто. <paramref name="Reason" /> — код
    /// <see cref="Suggestable" /> либо «модуль выключен»; на провод не уходит.</summary>
    public sealed record Silent(string Reason) : SuggestionPlan;

    /// <summary>Источники есть, но читателю закрыты все (403).</summary>
    public sealed record Forbidden(string Reason) : SuggestionPlan;

    /// <summary>
    /// Читать верхний уровень записей <paramref name="Types" /> — семьи объявившего поле (он в ней
    /// всегда, если не закрыт правами) — и поле по путям <paramref name="Nested" /> внутри записей
    /// типов-владельцев (issue #1532). Пустые оба — законный ответ: читать нечего.
    /// </summary>
    public sealed record Read(IReadOnlyList<Guid> Types, IReadOnlyList<NestedSource> Nested) : SuggestionPlan
    {
        public bool IsEmpty => Types.Count == 0 && Nested.Count == 0;
    }
}

/// <summary>
/// План источников подсказки — ЧИСТАЯ функция: без базы, по схемам типов и параметру доступа.
/// Отдельно от запроса, чтобы каждую ветку (семья, перекрытие, права) проверял модульный тест, а
/// не стенд.
/// </summary>
public static class SuggestionSources
{
    /// <summary>Право читать документы модуля исполнительной документации.</summary>
    public const string DocumentRead = "id.document.read";
    /// <summary>Право читать справочники ядра (общие данные).</summary>
    public const string CatalogRead = "core.catalog.read";
    /// <summary>Право заводить позицию номенклатуры коротким окном — шаг «Новая позиция».</summary>
    public const string NomenclatureEdit = "core.nomenclature.edit";
    /// <summary>Модуль, без которого документов нет.</summary>
    public const string DocumentsModule = "id";

    /// <summary>Причина «пусто» у семьи, все документы которой ушли с выключенным модулем.</summary>
    public const string ModuleOff = "moduleOff";

    /// <summary>
    /// План по типу, который назвал клиент, и ключу поля.
    ///
    /// <para><b>Тип — любой из семьи, где поле видно</b> (решение В-2): эффективная схема клиента не
    /// помнит, кто объявил поле. Объявившего сервер находит сам, поднимаясь по цепочке до типа, у
    /// которого ключ свой, и от него строит семью — поэтому «Производитель» вида номенклатуры
    /// подсказывает значения всех видов, унаследовавших это поле.</para>
    ///
    /// <para><b>Потомок, объявивший ключ своим или исключивший его, из семьи выпадает</b> (D1): его
    /// «Производитель» — другое поле с тем же именем, и смешивать их значения нельзя.</para>
    ///
    /// <para><b>Поле внутри чужих записей</b> (issue #1532): тип семьи, встреченный составным полем или
    /// строкой массива в схеме другого типа, даёт тому путь до поля (<see cref="SuggestionPaths" />).
    /// Читаются записи владельца, и права — его вида.</para>
    ///
    /// <para><b>Права — по виду типа</b> (D5): документы — при праве на чтение документов и
    /// включённом модуле, остальное — при праве на справочники; справочник номенклатуры открыт ещё и
    /// держателю права шага «Новая позиция» (решение В-1) — так же, как дверь номенклатуры отдаёт ему
    /// записи на выбор.</para>
    /// </summary>
    public static SuggestionPlan For(
        Guid typeId, string field, IReadOnlyDictionary<Guid, DocumentType> types, DataAccess access)
    {
        if (!types.TryGetValue(typeId, out var type))
            return new SuggestionPlan.Unknown("Такого типа нет.");
        var visible = DocumentTypeSchemaReader.Field(typeId, field, types);
        if (visible is null)
            return new SuggestionPlan.Unknown($"У типа «{type.Name}» нет поля «{field}».");

        // Видимое поле объявил кто-то в цепочке — иначе его не было бы в эффективной схеме.
        var declarer = DeclarerOf(typeId, field, types)!;
        var reason = Suggestable.Reason(visible.Type, visible.Computed, visible.Locked,
            SchemaTags.TagsOfOwnField(declarer.Schema, field));
        if (reason != Suggestable.Yes) return new SuggestionPlan.Silent(reason);

        // Семья — без оглядки на хранение: строка массива сама в таблице не лежит, но поле у неё есть.
        var members = types.Values
            .Where(t => DocumentTypeSchemaReader.IsSameOrDescendant(t.Id, declarer.Id, types)
                && DeclarerOf(t.Id, field, types)?.Id == declarer.Id
                && DocumentTypeSchemaReader.Field(t.Id, field, types) is not null)
            .Select(t => t.Id)
            .ToList();
        var nested = SuggestionPaths.For(members, field, types);

        var allowed = new List<Guid>();
        var owners = new List<NestedSource>();
        int byRights = 0, byModule = 0;
        foreach (var t in members.Select(id => types[id]).Where(t => t.Storage == TypeStorage.SharedObject))
            if (Admits(t)) allowed.Add(t.Id);
        foreach (var (owner, paths) in nested)
            if (types[owner].Storage == TypeStorage.SharedObject && Admits(types[owner]))
                owners.Add(new NestedSource(owner, paths));

        if (allowed.Count == 0 && owners.Count == 0 && byRights > 0)
            return new SuggestionPlan.Forbidden(
                $"Подсказки поля «{field}» берутся из записей, читать которые у вас нет права: "
                + $"документы — «{DocumentRead}», общие данные — «{CatalogRead}».");
        if (allowed.Count == 0 && owners.Count == 0 && byModule > 0) return new SuggestionPlan.Silent(ModuleOff);
        return new SuggestionPlan.Read(allowed, owners);

        // Открыт ли тип читателю; закрытый считается — модулем или правами, — чтобы различить ответы.
        bool Admits(DocumentType t)
        {
            if (t.Kind == DocumentTypeKind.Document)
            {
                if (!access.EnabledModules.Contains(DocumentsModule)) byModule++;
                else if (!access.Allows(DocumentRead)) byRights++;
                else return true;
                return false;
            }
            if (access.Allows(CatalogRead)
                || (access.Allows(NomenclatureEdit) && SchemaTags.TypeHasTag(t, types, FunctionalTag.RoleNomenclature)))
                return true;
            byRights++;
            return false;
        }
    }

    /// <summary>
    /// Тип, объявивший ключ своим: первый вверх по цепочке от названного, у кого поле в собственной
    /// схеме. null — ключа в цепочке нет.
    /// </summary>
    private static DocumentType? DeclarerOf(Guid typeId, string field, IReadOnlyDictionary<Guid, DocumentType> types)
    {
        var current = types.GetValueOrDefault(typeId);
        var guard = 0;
        while (current is not null && guard++ < 32)
        {
            if (DocumentTypeSchemaReader.OwnFields(current.Schema).Any(f => f.Key == field)) return current;
            current = current.ParentId is { } p ? types.GetValueOrDefault(p) : null;
        }
        return null;
    }
}
