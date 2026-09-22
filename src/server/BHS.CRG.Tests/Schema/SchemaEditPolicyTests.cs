using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Таблица ТЗ CORE-19.1 целиком: что администратор может сделать со схемой на каждом из трёх
/// уровней. Каждая строка таблицы — свой случай, каждый случай проверяется на всех трёх уровнях.
///
/// Тест написан таблицей, а не набором отдельных фактов, нарочно: правила читаются рядом, и
/// разрешение, случайно выданное не тому уровню, видно глазом — как пропущенная клетка.
///
/// ⚠️ Девятая строка таблицы («производный тип») здесь не проверяется: она не про схему, а про
/// создание ДРУГОГО типа, и живёт в обработчиках. Её стережёт <c>SchemaEditLevelTests</c>.
/// </summary>
public class SchemaEditPolicyTests
{
    // ── Исходная схема: два поля модуля и одно поле заказчика ─────────────────
    private const string Before = """
        {"fields":[
          {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
          {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
          {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
        ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
        """;

    /// <param name="Row">Строка таблицы ТЗ — она же объяснение, что проверяется.</param>
    /// <param name="Open">Разрешено ли на открытом уровне.</param>
    /// <param name="Extendable">…на расширяемом.</param>
    /// <param name="Closed">…на закрытом.</param>
    /// <param name="OwnBefore">Своя исходная схема, если общей для случая не хватает.</param>
    public record Case(string Row, string After, bool Open, bool Extendable, bool Closed,
        string? OwnBefore = null);

    public static TheoryData<Case, SchemaEditLevel, bool> Table()
    {
        var data = new TheoryData<Case, SchemaEditLevel, bool>();
        foreach (var c in Cases)
        {
            data.Add(c, SchemaEditLevel.Open, c.Open);
            data.Add(c, SchemaEditLevel.Extendable, c.Extendable);
            data.Add(c, SchemaEditLevel.Closed, c.Closed);
        }
        return data;
    }

    private static readonly Case[] Cases =
    [
        new("1. Добавить необязательное поле", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]},
              {"key":"Телефон","title":"Телефон","type":"string"}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: true, Closed: false),

        new("2. Добавить обязательное поле", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]},
              {"key":"Телефон","title":"Телефон","type":"string","required":true}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false),

        new("3а. Удалить поле модуля (оно же — переименовать)", """
            {"fields":[
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false),

        new("3б. Сменить вид поля модуля", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"number","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false),

        new("4. То же для СВОЕГО поля", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: true, Closed: false),

        new("5а. Подпись — можно везде, в том числе на поле модуля", """
            {"fields":[
              {"key":"Табельный","title":"Табельный №","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: true, Closed: true),

        new("5б. Порядок полей", """
            {"fields":[
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]},
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: true, Closed: false),

        new("5в. Группа полей", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Кадровое","keys":["Табельный"]}]}
            """, Open: true, Extendable: true, Closed: false),

        new("6. Тэги на поле модуля", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge","identity:1"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false),

        new("7. Тэги на СВОЁМ поле", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag","identity:1"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: true, Closed: false),

        new("8а. Варианты перечисления модуля — ДОБАВИТЬ", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь","Вахта"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: true, Closed: false),

        new("8б. Варианты перечисления модуля — УБРАТЬ", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false),

        // ── Сверх таблицы, по её же причинам ──────────────────────────────────
        // На открытом уровне это РАЗРЕШЕНО: там администратор меняет схему как хочет, и запертым
        // оказывается то, что он запер себе сам, — снять замок он может тем же редактором.
        new("+ Объявить своё поле полем модуля (замок себе)", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","origin":"module","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false),

        // ── Дыры, найденные ревью PR #1004 ────────────────────────────────────
        new("+ Сделать поле модуля расчётным (значение уходит из-под модуля)", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"],"computed":true,"expression":"1"},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false),

        new("+ Значение по умолчанию у поля модуля", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"],"defaultValue":"000"},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false),

        new("+ Тэги ТИПА (а не поля)", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}],"tags":["quality.doc"]}
            """, Open: true, Extendable: true, Closed: false),

        new("+ Переопределить унаследованное поле", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}],"fieldOverrides":{"Чужое":{"defaultValue":"х"}}}
            """, Open: true, Extendable: true, Closed: false),

        new("+ Исключить унаследованное поле", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}],"excludedFields":["Чужое"]}
            """, Open: true, Extendable: false, Closed: false),

        // Обратная сторона: СНЯТЬ исключение — то же добавление поля, только из родительского типа.
        new("+ Вернуть исключённое поле", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false,
            OwnBefore: """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"]}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}],"excludedFields":["Чужое"]}
            """),

        new("+ Сделать СВОЁ поле обязательным", """
            {"fields":[
              {"key":"Табельный","title":"Табельный номер","type":"string","origin":"module","tags":["work.badge"]},
              {"key":"Смена","title":"Смена","type":"enum","origin":"module","options":["День","Ночь"]},
              {"key":"Разряд","title":"Разряд","type":"string","tags":["own.tag"],"required":true}
            ],"groups":[{"name":"Общее","keys":["Табельный"]}]}
            """, Open: true, Extendable: false, Closed: false),
    ];

    [Theory]
    [MemberData(nameof(Table))]
    public void Таблица_уровней_правки_схемы(Case row, SchemaEditLevel level, bool allowed)
    {
        var refusals = SchemaEditPolicy.Refusals(
            JsonDocument.Parse(row.OwnBefore ?? Before), JsonDocument.Parse(row.After), level);

        if (allowed)
            Assert.True(refusals.Count == 0,
                $"«{row.Row}» на уровне «{level}» обязано быть РАЗРЕШЕНО, а отказано: "
                + string.Join("; ", refusals));
        else
            Assert.True(refusals.Count > 0,
                $"«{row.Row}» на уровне «{level}» обязано быть ЗАПРЕЩЕНО, а прошло без отказа");
    }

    /// <summary>
    /// Отказ называет ПОЛЕ и причину (ТЗ CORE-19.1). Без имени поля администратор видит «нельзя» и
    /// не знает, что именно откатывать, — а схема правится десятком изменений сразу.
    /// </summary>
    [Fact]
    public void Отказ_называет_поле_и_причину()
    {
        var after = Before.Replace("\"Табельный номер\"", "\"Табельный номер\"")
            .Replace("{\"key\":\"Табельный\"", "{\"key\":\"ТабельныйНомер\"");

        var refusals = SchemaEditPolicy.Refusals(
            JsonDocument.Parse(Before), JsonDocument.Parse(after), SchemaEditLevel.Extendable);

        var text = string.Join("; ", refusals);
        Assert.Contains("Табельный номер", text);
        Assert.Contains("опирается", text);
    }

    /// <summary>
    /// ⚠️ Та же схема, записанная ИНАЧЕ, — не изменение. Старая схема приходит из <c>jsonb</c>,
    /// который Postgres отдаёт со своими пробелами и своим порядком свойств, новая — из тела
    /// запроса, где клиент шлёт компактную запись. Сравнивай мы текстом — закрытый тип с группами
    /// отказывал бы на КАЖДОМ сохранении, и заметить это по тестам было нельзя: в них обе схемы
    /// разбирались из одинаково отформатированных литералов. Найдено ревью PR #1004.
    /// </summary>
    [Theory]
    [InlineData(SchemaEditLevel.Extendable)]
    [InlineData(SchemaEditLevel.Closed)]
    public void Та_же_схема_в_другом_форматировании_проходит(SchemaEditLevel level)
    {
        const string spaced = """
            { "fields" : [
                { "key" : "Табельный" , "title" : "Табельный номер" , "type" : "string" , "origin" : "module" , "tags" : [ "work.badge" ] } ,
                { "key" : "Смена" , "title" : "Смена" , "type" : "enum" , "origin" : "module" , "options" : [ "День" , "Ночь" ] } ,
                { "key" : "Разряд" , "title" : "Разряд" , "type" : "string" , "tags" : [ "own.tag" ] }
              ] , "groups" : [ { "name" : "Общее" , "keys" : [ "Табельный" ] } ] }
            """;

        var refusals = SchemaEditPolicy.Refusals(
            JsonDocument.Parse(spaced), JsonDocument.Parse(Before), level);

        Assert.True(refusals.Count == 0, "отказ на пробелах: " + string.Join("; ", refusals));
    }

    /// <summary>
    /// Схема, не изменившаяся вовсе, проходит на любом уровне. Иначе закрытый тип нельзя было бы
    /// даже пересохранить — и первая же правка печатной формы упиралась бы в отказ о полях.
    /// </summary>
    [Theory]
    [InlineData(SchemaEditLevel.Open)]
    [InlineData(SchemaEditLevel.Extendable)]
    [InlineData(SchemaEditLevel.Closed)]
    public void Неизменная_схема_проходит_на_любом_уровне(SchemaEditLevel level)
    {
        Assert.Empty(SchemaEditPolicy.Refusals(
            JsonDocument.Parse(Before), JsonDocument.Parse(Before), level));
    }
}
