using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Устойчивый парс ответа vision-LLM (issue #318): чистый JSON, JSON обёрнутый размышлениями/прозой
/// (thinking-модели типа qwen3-vl), markdown-fenced, пустой ответ; фильтрация по разрешённым полям.
/// </summary>
public class RecognitionSharedTests
{
    private static readonly IReadOnlyList<RecognitionField> Fields =
    [
        new("Наименование", "Наименование", "string"),
        new("Номер", "Номер", "string"),
    ];

    [Fact]
    public void ParseValues_CleanJson()
    {
        var r = RecognitionShared.ParseValues("{\"Наименование\":\"Кабель\",\"Номер\":\"123\"}", Fields);
        Assert.Equal("Кабель", r["Наименование"]);
        Assert.Equal("123", r["Номер"]);
    }

    [Fact]
    public void ParseValues_JsonWrappedInProse_IsExtracted()
    {
        // thinking-модель дописала размышления вокруг JSON — извлекаем сбалансированный объект.
        var text = "<think>Смотрю на изображение… поле Наименование = Кабель</think>\n"
                 + "Вот результат: {\"Наименование\": \"Кабель\", \"Номер\": \"123\"} — готово.";
        var r = RecognitionShared.ParseValues(text, Fields);
        Assert.Equal("Кабель", r["Наименование"]);
        Assert.Equal("123", r["Номер"]);
    }

    [Fact]
    public void ParseValues_FencedJson()
    {
        var r = RecognitionShared.ParseValues("```json\n{\"Наименование\":\"Кабель\"}\n```", Fields);
        Assert.Equal("Кабель", r["Наименование"]);
    }

    [Fact]
    public void ParseValues_EmptyOrNonJson_ReturnsEmpty()
    {
        Assert.Empty(RecognitionShared.ParseValues("", Fields));
        Assert.Empty(RecognitionShared.ParseValues("нет данных", Fields));
    }

    [Fact]
    public void ParseValues_FiltersToAllowedFields()
    {
        var r = RecognitionShared.ParseValues("{\"Наименование\":\"Кабель\",\"Лишнее\":\"x\"}", Fields);
        Assert.True(r.ContainsKey("Наименование"));
        Assert.False(r.ContainsKey("Лишнее"));
    }

    [Fact]
    public void ExtractFirstJsonObject_HandlesNestedAndStrings()
    {
        var s = "prefix {\"a\":\"}{\",\"b\":{\"c\":1}} suffix {\"ignored\":2}";
        Assert.Equal("{\"a\":\"}{\",\"b\":{\"c\":1}}", RecognitionShared.ExtractFirstJsonObject(s));
    }
}
