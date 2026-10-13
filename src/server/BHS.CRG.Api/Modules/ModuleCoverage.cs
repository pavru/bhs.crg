using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Modules;

/// <summary>Тип в ответе покрытия: носитель роли либо тот, кого ею можно назначить.</summary>
public sealed record CoverageType(Guid Id, string Name, string Code, string Kind);

/// <summary>Поле справочника, которое можно назначить потребности.</summary>
/// <param name="DeclaredIn">Тип, в схеме которого поле объявлено: тэг поля лежит ТАМ, и запись с экрана
/// правит его схему. У унаследованного поля это предок носителя роли.</param>
/// <param name="Fits">Вид поля подходит тэгу и поле не расчётное. Неподходящее в ответе остаётся: оно
/// может нести тэг сейчас — и тогда экран обязан назвать его, а не показать пустой выбор.</param>
/// <param name="Tagged">Тэг потребности сейчас стоит на этом поле.</param>
public sealed record CoverageField(
    string Key, string Title, string Type, CoverageType DeclaredIn, bool Fits, bool Tagged);

/// <summary>Состояние строки покрытия. Три, как в задаче: сопоставлено, не сопоставлено, нарушено.</summary>
public static class CoverageState
{
    public const string Mapped = "mapped";
    public const string Absent = "absent";
    public const string Violated = "violated";
}

/// <summary>Потребность в поле справочника и то, чем она сейчас обеспечена.</summary>
/// <param name="Fault">Что именно не так (<see cref="RoleFieldFault" />): расхождение у подтипа экран
/// сопоставления не чинит — оно лежит в схеме подтипа, — и обязан сказать об этом, а не дать выбор.</param>
/// <param name="Reason">Почему не обеспечена — тем же текстом, каким отказывает порт; <c>null</c> у
/// сопоставленной.</param>
/// <param name="Kinds">Виды полей, которые тэг допускает, — чтобы сказать «в типе нет поля вида …».</param>
public sealed record CoverageRow(
    string Tag, string Label, string Description, bool Required, string WithoutIt,
    string State, string Fault, string? Reason, string? Key, IReadOnlyList<string> Kinds,
    IReadOnlyList<CoverageField> Fields);

/// <summary>Потребность в справочнике: роль, её носитель и поля.</summary>
/// <param name="Type">Носитель роли; <c>null</c>, пока роль не обеспечена.</param>
/// <param name="Candidates">Кого можно назначить справочником. Непусто только когда роль не несёт никто:
/// перенос роли с типа на тип запрещён (решение владельца 13.10.2026), и предлагать его нечего.</param>
/// <param name="Rows">Поля. Пусто, пока роль не обеспечена: сопоставлять поле не в чем, и три красные
/// строки про одну причину были бы шумом.</param>
public sealed record CoverageGroup(
    string Role, string Label, string Purpose, string WithoutIt,
    string State, string? Reason, CoverageType? Type,
    IReadOnlyList<CoverageType> Candidates, IReadOnlyList<CoverageRow> Rows);

/// <summary>Покрытие потребностей одного модуля.</summary>
/// <param name="Total">Сколько обязательного модуль объявил: справочники и их обязательные поля.</param>
/// <param name="Unmet">Сколько из этого не обеспечено. Поля необеспеченного справочника считаются
/// тоже: «не назначено 1 из 4» про установку без единого справочника было бы неправдой.</param>
/// <param name="NotWorking">Что из-за этого не работает — тексты модуля, без повторов. У
/// необеспеченного справочника один текст — его собственный, а не по тексту на каждое поле.</param>
public sealed record ModuleCoverageView(
    string Code, string Title, int Total, int Unmet, IReadOnlyList<string> NotWorking,
    IReadOnlyList<CoverageGroup> Groups);

/// <summary>
/// Покрытие потребностей модулей справочниками ядра (ТЗ TYPE-23.1, issue #1101): что модулю нужно,
/// чем это обеспечено и что не работает, пока не обеспечено.
///
/// <para><b>Своего расчёта здесь нет.</b> Носителя роли и поле по тэгу находит
/// <see cref="RoleResolution" /> — та же функция, которой читает порт модуля. Считай экран по-своему,
/// он однажды сказал бы «сопоставлено» про поле, которого порт не находит, — ровно та неправда, ради
/// устранения которой роль заведена.</para>
///
/// <para>Чистая функция: типы и потребности приходят снаружи.</para>
/// </summary>
public static class ModuleCoverage
{
    public static ModuleCoverageView Of(
        IAppModule module, TagCatalog tags, IReadOnlyList<DocumentType> all)
    {
        var groups = module.Needs.Select(need => Group(need, tags, all)).ToList();

        int total = 0, unmet = 0;
        var notWorking = new List<string>();
        foreach (var (need, group) in module.Needs.Zip(groups))
        {
            var required = need.Fields.Count(f => f.Required);
            total += 1 + required;

            if (group.State != CoverageState.Mapped)
            {
                unmet += 1 + required;
                notWorking.Add(need.WithoutIt);
                continue;
            }

            foreach (var row in group.Rows.Where(r => r.Required && r.State != CoverageState.Mapped))
            {
                unmet++;
                notWorking.Add(row.WithoutIt);
            }
        }

        return new(module.Code, module.Title, total, unmet,
            [.. notWorking.Distinct(StringComparer.Ordinal)], groups);
    }

    private static CoverageGroup Group(ModuleRoleNeed need, TagCatalog tags, IReadOnlyList<DocumentType> all)
    {
        var roleTag = tags.Find(need.Role)!;
        var role = RoleResolution.Role(need.Role, all);

        if (role.Root is not { } root)
        {
            // Никто не несёт — «не сопоставлено»; несут двое или негодный — «нарушено»: назначить
            // справочник здесь нельзя, сначала надо снять лишнее в редакторе типа.
            var none = role.Bearers.Count == 0;
            return new(need.Role, roleTag.Label, need.Purpose, need.WithoutIt,
                none ? CoverageState.Absent : CoverageState.Violated, role.Describe(roleTag), null,
                none ? Candidates(tags, all) : [], []);
        }

        var byId = all.ToDictionary(t => t.Id);
        var fields = DocumentTypeSchemaReader.EffectiveFields(root.Id, byId);
        var rows = need.Fields.Select(f =>
        {
            var tag = tags.Find(f.Tag)!;
            var found = RoleResolution.Field(role, tag, all);
            var state = found.Fault switch
            {
                RoleFieldFault.None => CoverageState.Mapped,
                RoleFieldFault.Absent => CoverageState.Absent,
                _ => CoverageState.Violated,
            };

            return new CoverageRow(f.Tag, tag.Label, tag.Description, f.Required, f.WithoutIt,
                state, found.Fault.ToString(), found.Describe(tag), found.Key, tag.AppliesTo,
                [.. fields.Select(field => new CoverageField(
                    field.Key, field.Title is { Length: > 0 } title ? title : field.Key, field.Type,
                    View(DeclaredIn(root, field.Key, byId)),
                    Fits: !field.Computed && (tag.AppliesTo.Length == 0 || tag.AppliesTo.Contains(field.Type, StringComparer.Ordinal)),
                    Tagged: found.Keys.Contains(field.Key, StringComparer.Ordinal)))]);
        }).ToList();

        return new(need.Role, roleTag.Label, need.Purpose, need.WithoutIt,
            CoverageState.Mapped, null, View(root), [], rows);
    }

    /// <summary>
    /// Кого можно назначить справочником: годный тип, который сам роли не несёт и не входит в семью
    /// другого справочника. Организации и номенклатура одним типом быть не могут — записи у них разные.
    /// </summary>
    private static List<CoverageType> Candidates(TagCatalog tags, IReadOnlyList<DocumentType> all)
    {
        var taken = tags.All.Where(t => tags.IsRole(t.Code))
            .Select(t => RoleResolution.Role(t.Code, all))
            .SelectMany(r => r.Root is null ? r.Bearers.Select(b => b.Id) : r.Family.Keys)
            .ToHashSet();

        return [.. all.Where(t => RoleResolution.IsDirectory(t) && !taken.Contains(t.Id))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(View)];
    }

    /// <summary>
    /// Тип, в схеме которого поле объявлено: ближайший вверх по цепочке, начиная с носителя роли.
    /// Цикл в цепочке родителей базой не запрещён — обход помнит пройденное.
    /// </summary>
    private static DocumentType DeclaredIn(DocumentType root, string key, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var seen = new HashSet<Guid>();
        for (var cur = root; cur is not null && seen.Add(cur.Id);
             cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
            if (OwnFieldKeys(cur.Schema).Contains(key, StringComparer.Ordinal)) return cur;

        return root;
    }

    private static IEnumerable<string> OwnFieldKeys(JsonDocument schema) =>
        schema.RootElement.ValueKind == JsonValueKind.Object
        && schema.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.Object && f.TryGetProperty("key", out var k)
                            && k.ValueKind == JsonValueKind.String)
                .Select(f => f.GetProperty("key").GetString()!)
            : [];

    private static CoverageType View(DocumentType type) => new(type.Id, type.Name, type.Code, type.Kind.ToString());
}
