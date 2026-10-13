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
        var changed = new List<(DocumentType, JsonDocument)>();

        foreach (var rule in Rules)
        {
            // Код — посимвольно, как в миграции: там сравнение SQL, и «организация» с маленькой
            // буквы она не тронула бы тоже.
            var type = all.FirstOrDefault(t => t.Code == rule.Code && t.Kind == DocumentTypeKind.Composite);
            if (type is null || JsonNode.Parse(type.Schema.RootElement.GetRawText()) is not JsonObject schema) continue;

            var touched = false;
            var bears = SchemaTags.SchemaHasTypeTag(type.Schema, rule.Role);

            if (!bears && !all.Any(t => SchemaTags.SchemaHasTypeTag(t.Schema, rule.Role)))
            {
                var tags = schema["tags"] as JsonArray ?? [];
                tags.Add(rule.Role);
                schema["tags"] = tags;
                touched = bears = true;
            }

            // Поля — только у носителя роли: тэг роли на типе без роли никто не прочитает.
            if (bears && schema["fields"] is JsonArray fields)
                foreach (var field in rule.Fields)
                    touched |= Tag(fields, field);

            if (touched) changed.Add((type, JsonDocument.Parse(schema.ToJsonString())));
        }

        return changed;
    }

    private static bool Tag(JsonArray fields, FieldRule rule)
    {
        var objects = fields.OfType<JsonObject>().ToList();

        // Тэг уже стоит на каком-то поле типа — не важно, на каком: это решение человека.
        if (objects.Any(f => f["tags"] is JsonArray tags && tags.Any(t => IsTag(t, rule.Tag)))) return false;

        var target = objects.FirstOrDefault(f => Text(f["key"]) == rule.Key);
        if (target is null || !rule.Kinds.Contains(Text(target["type"]) ?? "", StringComparer.Ordinal)) return false;

        var own = target["tags"] as JsonArray ?? [];
        own.Add(rule.Tag);
        target["tags"] = own;
        return true;
    }

    private static bool IsTag(JsonNode? node, string tag) => Text(node) is { } text && TagCode.CodeOf(text) == tag;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
