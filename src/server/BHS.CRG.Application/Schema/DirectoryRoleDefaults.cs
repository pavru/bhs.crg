using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Роли справочников и тэги их полей для установок, заведённых ДО появления ролей (issue #1101).
///
/// <para>Раньше код находил справочники по именам: тип «Организация», его поля «ИНН» и
/// «Наименование», тип «Номенклатура». Теперь — по тэгам, и на действующей установке этих тэгов нет:
/// без переноса обновление остановило бы сопоставление поставщика и выбор позиции у каждого
/// заказчика, а восстановленная копия поднялась бы зелёной и без справочников.</para>
///
/// <para><b>Правило применяется дважды и обязано быть одним.</b> Миграция ядра
/// <c>DirectoryRoles</c> делает это в SQL — один раз на установку. Восстановление копии, снятой до
/// переноса, делает то же этой функцией. Что два исполнения дают один результат, сверяет
/// <c>DirectoryRoleDefaultsTests</c> на одних и тех же схемах.</para>
///
/// <para>⚠️ При старте правило НЕ применяется, и применять его там нельзя: снять роль или
/// переставить тэг — решение администратора, а возврат при каждом запуске отменял бы его молча
/// (тот же довод, что у <c>CoreOwnedTypes</c>).</para>
///
/// <para>Правило осторожное: ставит только туда, где свободно. Роль, которую уже несёт любой тип,
/// не трогается; тэг, который уже стоит на каком-то поле типа, — тоже; поле не того вида тэга не
/// получает. Чего нет, того нет: установка без типа «Организация» остаётся без роли и честно
/// говорит «не назначено».</para>
///
/// <para><b>Находит то же, что находил прежний код</b> (ревью PR #1317). Тот искал тип без учёта
/// регистра, а поле — по схеме с наследованием. Поэтому и здесь: код типа сравнивается без регистра,
/// а поле ищется у носителя роли и ВЫШЕ по цепочке родителей — тэг получает ближайшее объявление.
/// Иначе установка, где «ИНН» объявлен у родителя «Организации», обновилась бы зелёной и перестала
/// бы сопоставлять поставщика.</para>
/// </summary>
public static class DirectoryRoleDefaults
{
    /// <param name="Kinds">Виды поля, которым тэг ставится. Совпадают с <c>AppliesTo</c> тэга в
    /// реестре — сверяет тест: правило, ставящее тэг туда, где его не предлагает редактор, дало бы
    /// поле, которое порт не читает.</param>
    public sealed record FieldRule(string Key, string Tag, IReadOnlyList<string> Kinds);

    public sealed record TypeRule(string Code, string Role, IReadOnlyList<FieldRule> Fields);

    public static readonly IReadOnlyList<TypeRule> Rules =
    [
        new("Организация", FunctionalTag.RoleOrganization,
        [
            new("ИНН", FunctionalTag.OrgTaxId, ["string", "text", "number", "primitive"]),
            new("Наименование", FunctionalTag.OrgName, ["string", "text", "complex"]),
        ]),
        new("Номенклатура", FunctionalTag.RoleNomenclature, []),
    ];

    /// <summary>
    /// Что изменить: тип и его новая схема. Пусто — менять нечего. Сами типы не трогаются: записывает
    /// тот, кто владеет транзакцией.
    /// </summary>
    public static IReadOnlyList<(DocumentType Type, JsonDocument Schema)> Apply(IReadOnlyList<DocumentType> all)
    {
        // Рабочие копии схем: два правила полей и роль могут лечь в один тип, а поле — в родителя.
        var schemas = new Dictionary<Guid, JsonObject>();
        var touched = new HashSet<Guid>();
        var byId = all.ToDictionary(t => t.Id);

        JsonObject? SchemaOf(DocumentType type)
        {
            if (schemas.TryGetValue(type.Id, out var known)) return known;
            if (JsonNode.Parse(type.Schema.RootElement.GetRawText()) is not JsonObject parsed) return null;
            return schemas[type.Id] = parsed;
        }

        foreach (var rule in Rules)
        {
            // Без регистра — как искал прежний код; двух типов, различных только регистром, не
            // бывает: коды уникальны тоже без него.
            var type = all.FirstOrDefault(t =>
                string.Equals(t.Code, rule.Code, StringComparison.OrdinalIgnoreCase)
                && t.Kind == DocumentTypeKind.Composite);
            if (type is null || SchemaOf(type) is not { } schema) continue;

            var bears = SchemaTags.SchemaHasTypeTag(type.Schema, rule.Role);

            if (!bears && !all.Any(t => SchemaTags.SchemaHasTypeTag(t.Schema, rule.Role)))
            {
                var tags = schema["tags"] as JsonArray ?? [];
                tags.Add(rule.Role);
                schema["tags"] = tags;
                touched.Add(type.Id);
                bears = true;
            }

            // Поля — только у носителя роли: тэг роли на типе без роли никто не прочитает.
            if (!bears) continue;

            // Носитель и его предки, ближайший первым. Цикл в цепочке родителей базе ничем не
            // запрещён — обход помнит пройденное.
            var chain = new List<(DocumentType Type, JsonArray Fields)>();
            for (var cur = type; cur is not null && chain.All(c => c.Type.Id != cur.Id);
                 cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
                if (SchemaOf(cur)?["fields"] is JsonArray fields)
                    chain.Add((cur, fields));

            foreach (var field in rule.Fields)
                if (Tag(chain, field) is { } owner)
                    touched.Add(owner);
        }

        return [.. all.Where(t => touched.Contains(t.Id))
            .Select(t => (t, JsonDocument.Parse(schemas[t.Id].ToJsonString())))];
    }

    /// <returns>Тип, чьё поле получило тэг; <c>null</c> — не получило ничьё.</returns>
    private static Guid? Tag(IReadOnlyList<(DocumentType Type, JsonArray Fields)> chain, FieldRule rule)
    {
        // Тэг уже стоит на каком-то поле цепочки — не важно, на каком: это решение человека.
        if (chain.Any(c => c.Fields.OfType<JsonObject>()
                .Any(f => f["tags"] is JsonArray tags && tags.Any(t => IsTag(t, rule.Tag)))))
            return null;

        // Ближайшее объявление поля: у самого носителя, иначе у родителя, и так вверх.
        foreach (var (type, fields) in chain)
        {
            var target = fields.OfType<JsonObject>().FirstOrDefault(f => Text(f["key"]) == rule.Key);
            if (target is null) continue;
            if (!rule.Kinds.Contains(Text(target["type"]) ?? "", StringComparer.Ordinal)) return null;

            var own = target["tags"] as JsonArray ?? [];
            own.Add(rule.Tag);
            target["tags"] = own;
            return type.Id;
        }

        return null;
    }

    private static bool IsTag(JsonNode? node, string tag) => Text(node) is { } text && TagCode.CodeOf(text) == tag;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
