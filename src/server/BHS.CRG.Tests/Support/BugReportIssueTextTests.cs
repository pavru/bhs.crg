using System.Text.Json;
using BHS.CRG.Application.Support;

namespace BHS.CRG.Tests.Support;

/// <summary>
/// Заготовка текста issue (issue #834).
///
/// Проверяется отдельно от сервиса, потому что это единственное место, где решается, ЧТО из
/// сообщения увидит публичный репозиторий. Ошибка здесь не падает и не логируется — она просто
/// однажды публикует лишнее.
/// </summary>
public class BugReportIssueTextTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Build_PutsUserWordsFirst_ThenTechnicalSection()
    {
        var text = BugReportIssueText.Build("Кнопка «Сохранить» не нажимается.", null, hasScreenshot: false);

        Assert.StartsWith("Кнопка «Сохранить» не нажимается.", text);
        Assert.Contains("## Техническая информация", text);
        // Техблока не прислали — говорим об этом прямо. Пустой раздел читался бы как «всё в порядке».
        Assert.Contains("не прислал техблок", text);
    }

    [Fact]
    public void Build_ShowsBothVersions_TabAndServer()
    {
        var tech = Json("""
            {"version":"0.143.0","commit":"a1b2c3d","route":"/document-sets","userAgent":"Firefox/141",
             "viewport":"1920×1080","server":{"version":"0.144.0","commit":"9f8e7d6"}}
            """);

        var text = BugReportIssueText.Build("Не открывается комплект.", tech, hasScreenshot: false);

        // Обе версии порознь: вкладка SPA переживает обновление сервера, и расхождение этих двух
        // само по себе объясняет часть сообщений.
        Assert.Contains("Версия при загрузке страницы: 0.143.0 (сборка a1b2c3d)", text);
        Assert.Contains("Версия сервера сейчас: 0.144.0 (сборка 9f8e7d6)", text);
        Assert.Contains("Экран: /document-sets", text);
        Assert.Contains("Браузер: Firefox/141", text);
    }

    [Fact]
    public void Build_RendersApiErrors_WithTraceId()
    {
        var tech = Json("""
            {"apiErrors":[
              {"at":"12:31:05","method":"POST","url":"/api/generate/1","status":500,"traceId":"0HNF:0000A"}
            ]}
            """);

        var text = BugReportIssueText.Build("Не генерируется PDF.", tech, hasScreenshot: false);

        // Идентификатор запроса — то единственное, по чему сообщение находится в логе api.
        Assert.Contains("0HNF:0000A", text);
        Assert.Contains("| POST `/api/generate/1` | 500 |", text);
    }

    [Fact]
    public void Build_WrapsStackInCodeBlock()
    {
        var tech = Json("""{"stack":"TypeError: x is not a function\n  at Foo (bundle.js:1:2)"}""");

        var text = BugReportIssueText.Build("Белый экран.", tech, hasScreenshot: false);

        Assert.Contains("### Стек сбоя интерфейса", text);
        Assert.Contains("```\nTypeError: x is not a function", text);
    }

    /// <summary>
    /// Снимок остаётся у администратора — но разработчик должен знать, что он существует: иначе
    /// «попросите скриншот» невозможно даже произнести.
    /// </summary>
    [Fact]
    public void Build_MentionsScreenshot_ButNeverAttachesIt()
    {
        var text = BugReportIssueText.Build("Съехала таблица.", null, hasScreenshot: true);

        Assert.Contains("Снимок экрана: у администратора", text);
        Assert.Contains("в issue не передаётся", text);
    }

    /// <summary>
    /// Пустые поля техблока не превращаются в строки-пустышки: «Экран: » в публичном issue выглядит
    /// как недоделка, а сообщает ровно ничего.
    /// </summary>
    [Fact]
    public void Build_SkipsEmptyFields()
    {
        var tech = Json("""{"version":"0.143.0","route":"","userAgent":null}""");

        var text = BugReportIssueText.Build("Что-то не так.", tech, hasScreenshot: false);

        Assert.DoesNotContain("Экран:", text);
        Assert.DoesNotContain("Браузер:", text);
        Assert.Contains("Версия при загрузке страницы: 0.143.0", text);
    }
}
