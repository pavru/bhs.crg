using System.Text.Json.Nodes;
using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Tests.QualityDocs;

/// <summary>
/// Распознанное значение доезжает до реквизитов В ОБЪЯВЛЕННОМ ВИДЕ (issue #1005).
///
/// Обе половины обязательны. Первая — что приведение вообще происходит; вторая — что оно не жадное:
/// сторож, проверяющий только первое, остался бы зелёным, начни мы выбрасывать всё, что не
/// разобралось, или читать «10 м» как 10.
/// </summary>
public class RecognizedValuesTests
{
    private static IReadOnlyDictionary<string, JsonNode?> Shape(
        (string Path, string Type) field, string? answer)
        => RecognizedValues.InDeclaredShape(
            new Dictionary<string, string?> { [field.Path] = answer },
            [new RecognitionField(field.Path, field.Path, field.Type)]);

    [Theory]
    // Ради чего задача: модель отвечает текстом, поле объявлено числом.
    [InlineData("number", "3", "3")]
    [InlineData("number", "12,5", "12.5")]
    [InlineData("bool", "да", "true")]
    [InlineData("boolean", "нет", "false")]
    [InlineData("date", "01.02.2026", "\"2026-02-01\"")]
    public void Значение_приводится_к_объявленному_виду(string type, string answer, string expected)
    {
        var shaped = Shape(("Поле", type), answer);

        Assert.Equal(expected, shaped["Поле"]!.ToJsonString());
    }

    [Theory]
    // Прочитанное из скана терять нельзя: расхождение покажет форма и найдёт аудит — а вот
    // молчаливая потеря значения не видна нигде.
    [InlineData("number", "три")]
    [InlineData("number", "10 м")]
    [InlineData("date", "позавчера")]
    [InlineData("bool", "возможно")]
    public void Неразобравшееся_остаётся_строкой_как_есть(string type, string answer)
    {
        var shaped = Shape(("Поле", type), answer);

        Assert.Equal(answer, shaped["Поле"]!.GetValue<string>());
    }

    /// <summary>
    /// Вариант перечисления приведением не угадывается: подпись отображает в код клиент, у которого
    /// есть реестр (issue #654). Тронь мы его здесь — в реквизиты легла бы подпись вместо кода.
    /// </summary>
    [Fact]
    public void Перечисление_остаётся_подписью_для_клиента()
    {
        var shaped = Shape(("Вид", "enum"), "Сертификат соответствия");

        Assert.Equal("Сертификат соответствия", shaped["Вид"]!.GetValue<string>());
    }

    /// <summary>Поле, о котором не просили, приводить не к чему — доезжает текстом, а не пропадает.</summary>
    [Fact]
    public void Значение_без_объявленного_поля_доезжает_текстом()
    {
        var shaped = RecognizedValues.InDeclaredShape(
            new Dictionary<string, string?> { ["Чужое"] = "7" },
            [new RecognitionField("Поле", "Поле", "number")]);

        Assert.Equal("7", shaped["Чужое"]!.GetValue<string>());
    }

    /// <summary>Пустой ответ по полю — пустое значение, а не строка «null» и не пропуск ключа.</summary>
    [Fact]
    public void Пустой_ответ_остаётся_пустым()
    {
        var shaped = Shape(("Поле", "number"), null);

        Assert.True(shaped.ContainsKey("Поле"));
        Assert.Null(shaped["Поле"]);
    }
}
