using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// «Ответа не было» против «ответ пуст» (issue #802).
///
/// Главное здесь — граница, а не сам разбор. Пустой JSON это ЗАКОННЫЙ результат: на листе без штампа
/// модель честно отвечает, что полей нет, и объявлять это отказом значило бы кричать на каждом
/// графическом листе альбома. Отказ — когда ответа не пришло вовсе или он обрезан на середине;
/// именно этот случай до сих пор гасился как «ноль полей» и уходил успехом.
/// </summary>
public class SilentAnswerTests
{
    private static readonly IReadOnlyList<RecognitionField> Fields =
    [
        new RecognitionField("Организация", "Организация", "string"),
        new RecognitionField("Шифр", "Шифр", "string"),
    ];

    [Fact]
    public void EmptyJson_IsAValidAnswer()
    {
        // Лист без штампа: полей нет, и это не отказ.
        Assert.Empty(RecognitionShared.ParseValues("{}", Fields));
        Assert.Empty(RecognitionShared.ParseValues("{\"Организация\": \"\", \"Шифр\": \"\"}", Fields));
    }

    [Fact]
    public void TruncatedJson_IsSilence_NotZeroFields()
    {
        // Ровно то, что происходило со спецификацией на сотню строк: ответ оборван лимитом, объект не
        // закрыт, разбор падал — и «ноль полей» уходило как успешный результат.
        var truncated = "{\"Организация\": \"ООО Проект\", \"Шифр\": \"25-04-063-";
        var ex = Assert.Throws<RecognitionSilentException>(() => RecognitionShared.ParseValues(truncated, Fields));
        Assert.Contains("обрезан", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Извините, я не могу обработать этот документ.")]
    public void NoJsonAtAll_IsSilence(string raw)
        => Assert.Throws<RecognitionSilentException>(() => RecognitionShared.ParseValues(raw, Fields));

    [Fact]
    public void ArrayInsteadOfObject_IsSilence()
    {
        // Модель ответила массивом вместо объекта полей: разобрать нечего, и молчаливый пустой
        // словарь тут так же неотличим от «полей нет», как и обрезанный JSON.
        Assert.Throws<RecognitionSilentException>(() => RecognitionShared.ParseValues("[1, 2, 3]", Fields));
    }

    [Fact]
    public void ProseAroundJson_StillParses()
    {
        // Поведение из #318 сохраняется: JSON, обёрнутый размышлениями, по-прежнему разбирается.
        var values = RecognitionShared.ParseValues(
            "Думаю, это выглядит так:\n{\"Шифр\": \"25-04-063-ЭМ\"}\nНадеюсь, помог.", Fields);
        Assert.Equal("25-04-063-ЭМ", values["Шифр"]);
    }

    [Fact]
    public void TryParse_NamesTheProblem_WithoutThrowing()
    {
        Assert.False(RecognitionShared.TryParseValues("", Fields, out _, out var problem));
        Assert.Contains("пустой ответ", problem!);
        Assert.True(RecognitionShared.TryParseValues("{}", Fields, out var values, out var none));
        Assert.Empty(values);
        Assert.Null(none);
    }

    [Fact]
    public void SilenceIsAnUnavailability_ForEveryoneWhoDoesNotCare()
    {
        // Цепочка ловит базовый тип и переходит к следующему движку — ради этого Silent и наследник.
        Assert.IsAssignableFrom<RecognitionUnavailableException>(new RecognitionSilentException("x"));
    }
}

/// <summary>
/// Вход, который не помещается в контекст локальной модели (issue #802): раньше он молча обрезался —
/// модель получала неполный документ и добросовестно отвечала по нему.
/// </summary>
public class OllamaContextLimitTests
{
    [Fact]
    public async Task TooManyPages_AreRefused_NotSilentlyTruncated()
    {
        // Пятистраничный под-PDF таблицы: 2048 + 5*4608 + 8192 больше 32768 — в один вызов не влезет.
        var engine = new OllamaRecognizerEngine(new HttpClient(new ThrowingHandler()),
            new StubSettings("qwen2.5vl:7b"), NullLogger<OllamaRecognizerEngine>.Instance);

        var ex = await Assert.ThrowsAsync<RecognitionUnavailableException>(() =>
            engine.RecognizeRawAsync(FivePagePdf(), "application/pdf", [], null, CancellationToken.None));

        Assert.Contains("не помещаются в отведённый контекст", ex.Message);
    }

    /// <summary>Пять страниц А4 — самый маленький настоящий PDF, который даёт нужное число картинок.</summary>
    private static byte[] FivePagePdf()
    {
        using var doc = new PdfSharpCore.Pdf.PdfDocument();
        for (var i = 0; i < 5; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Запрос до сети дойти не должен: отказ считается ДО обращения к модели.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Невлезающий вход не должен доходить до модели.");
    }

    private sealed class StubSettings(string model) : IIntegrationSettings
    {
        public Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default)
        {
            var m = new IntegrationSettingsModel();
            m.Recognition["Ollama"] = new IntegrationEngine { Enabled = true, Model = model };
            return Task.FromResult(m);
        }

        public Task SaveAsync(IntegrationSettingsModel update, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveSmtpAsync(SmtpSettings smtp, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }
}

/// <summary>
/// Отсечка «движок замолчал» на постраничном прогоне (issue #802): молчание страничное, но не
/// бесконечное, и прекращение НЕ выбрасывает уже распознанное.
/// </summary>
public class PageFailureTrackerTests
{
    private static RecognitionSilentException Silent() => new("ответа нет");

    [Fact]
    public void SinglePageSilence_DoesNotStopTheRun()
    {
        var t = new PageFailureTracker();
        t.PageFailed(Silent());
        Assert.False(t.ShouldStop);
        Assert.Equal(1, t.FailedPages);
    }

    [Fact]
    public void ThreeInARow_StopTheRun()
    {
        var t = new PageFailureTracker();
        t.PageFailed(Silent());
        t.PageFailed(Silent());
        Assert.False(t.ShouldStop);
        t.PageFailed(Silent());
        Assert.True(t.ShouldStop);
        Assert.Contains("перестал отвечать", t.StopReason);
    }

    [Fact]
    public void AnAnswerInBetween_ResetsTheStreak()
    {
        // Два молчания, ответ, два молчания — движок отвечает, просто не на всех листах. Прекращать
        // такой прогон значило бы отбирать у человека работающее распознавание.
        var t = new PageFailureTracker();
        t.PageFailed(Silent());
        t.PageFailed(Silent());
        t.PageSucceeded();
        t.PageFailed(Silent());
        t.PageFailed(Silent());
        Assert.False(t.ShouldStop);
        Assert.Equal(4, t.FailedPages);
    }

    [Fact]
    public void NonSilentFailures_DoNotCountTowardsTheStreak()
    {
        // Таймаут и отказ считаются отдельно: у них своя отсечка (прогон прекращается на первой
        // странице), и складывать их с молчанием значило бы прекращать прогон по сумме разнородного.
        var t = new PageFailureTracker();
        t.PageFailed(Silent());
        t.PageFailed(new RecognitionTimeoutException("срок"), silent: false);
        t.PageFailed(Silent());
        t.PageFailed(Silent());
        Assert.False(t.ShouldStop);
    }

    [Fact]
    public void FirstReasonWins()
    {
        // Первая причина объясняет, с чего прогон посыпался; последняя чаще всего лишь следствие.
        var t = new PageFailureTracker();
        t.PageFailed(new RecognitionSilentException("лимит ответа исчерпан"));
        t.PageFailed(new RecognitionSilentException("пустой ответ"));
        Assert.Equal("лимит ответа исчерпан", t.FirstReason);
    }
}

/// <summary>
/// Ответ, уехавший в размышления модели (issue #318, #803): у думающих моделей содержательный текст
/// приходит не в том поле, и до этой правки конвейер считал такой ответ отсутствующим.
/// </summary>
public class ThinkingRescueTests
{
    private static readonly IReadOnlyList<RecognitionField> Fields =
        [new RecognitionField("Шифр", "Шифр", "string")];

    [Fact]
    public void JsonFromThinking_IsAnAnswer()
    {
        // Проверено на живой модели 2026-08-20: при пустом `response` в `thinking` лежит ровно тот
        // JSON, который должен был прийти. Достаём его тем же разбором, что и из ответа.
        const string thinking = "Хорошо, посмотрим на штамп. Шифр читается как 25-04-063-ЭМ.\n" +
                                "{\"Шифр\": \"25-04-063-ЭМ\"}";
        var rescued = RecognitionShared.ExtractFirstJsonObject(thinking);
        Assert.NotNull(rescued);
        Assert.Equal("25-04-063-ЭМ", RecognitionShared.ParseValues(rescued!, Fields)["Шифр"]);
    }

    [Fact]
    public void ThinkingWithoutJson_StaysSilence()
    {
        // Второй случай пустого ответа, который фоллбэком НЕ лечится: модель упёрлась в лимит и до
        // ответа не дошла — в размышлениях оборванное рассуждение, доставать оттуда нечего.
        const string thinking = "Так, мне нужно рассмотреть штамп. Сначала посмотрю на правый нижний угол, там";
        Assert.Null(RecognitionShared.ExtractFirstJsonObject(thinking));
    }
}
