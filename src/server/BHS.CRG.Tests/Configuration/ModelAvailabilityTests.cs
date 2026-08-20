using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Settings;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// «Модели, которую вы выбрали, у поставщика нет» — и границы, за которые это утверждение выходить
/// не должно (issue #799).
///
/// Разбор живого случая: документ не распознавался, потому что в настройках стояла модель Gemini,
/// снятая с обслуживания, а запасная Ollama была «настроена» моделью, которой на машине нет. Ни то,
/// ни другое интерфейс не показывал — оба движка выглядели рабочими.
/// </summary>
public class ModelAvailabilityTests
{
    private static IntegrationEngine Gemini(string model) => new() { Enabled = true, ApiKey = "k", Model = model };
    private static IntegrationEngine Ollama(string model) => new() { Enabled = true, Model = model };

    [Fact]
    public void Unknown_SaysNothing()
    {
        // Главная граница. «Не проверили» — это не «плохо»: Ollama может быть не запущена, у ключа
        // могли кончиться деньги, сеть могла молчать. Приняв это за «модели нет», интерфейс пошлёт
        // чинить то, что не сломано, — и первым же таким случаем обесценит саму пометку.
        Assert.Null(EngineReadiness.ModelIssue("Ollama", Ollama("qwen3-vl"), ModelStatus.Unknown));
        Assert.Null(EngineReadiness.ModelIssue("Gemini", Gemini("gemini-3.5-flash"), ModelStatus.Unknown));
    }

    [Fact]
    public void Ok_SaysNothing()
    {
        Assert.Null(EngineReadiness.ModelIssue("Gemini", Gemini("gemini-3.5-flash"), ModelStatus.Ok));
    }

    [Fact]
    public void Gone_NamesModelAndCarriesAdvice()
    {
        var issue = EngineReadiness.ModelIssue("Gemini", Gemini("gemini-2.5-flash-lite"),
            new ModelStatus(ModelState.Gone, "поставщик рекомендует gemini-3.5-flash-lite"));
        Assert.Contains("gemini-2.5-flash-lite", issue);
        // Совет поставщика — самое полезное в сообщении: он называет, на что менять.
        Assert.Contains("gemini-3.5-flash-lite", issue);
    }

    [Fact]
    public void Ollama_GoneReadsAsNotDownloaded()
    {
        // Локальной модели не «нет у поставщика» — её не скачали. Разные слова для разных действий.
        var issue = EngineReadiness.ModelIssue("Ollama", Ollama("gemma4:latest"),
            new ModelStatus(ModelState.Gone, "скачайте её: ollama pull gemma4:latest"));
        Assert.Contains("не скачана", issue);
    }

    [Fact]
    public void UnconfiguredEngine_ComplainsAboutConfigurationOnly()
    {
        // У движка без ключа претензия одна — «не задан ключ». Добавлять к ней вторую, про модель,
        // значит спорить с самим собой: движок и так не участвует.
        var noKey = new IntegrationEngine { Enabled = true, Model = "gemini-3.5-flash" };
        Assert.Equal("не задан ключ", EngineReadiness.MissingForRecognition("Gemini", noKey));
        Assert.Null(EngineReadiness.ModelIssue("Gemini", noKey, new ModelStatus(ModelState.Gone)));
    }

    [Fact]
    public void SameModel_TreatsMissingTagAsLatest()
    {
        // Ollama перечисляет «qwen3-vl:latest», человек в настройках пишет «qwen3-vl». Это одна модель,
        // и объявить её отсутствующей значило бы придраться к записи.
        Assert.True(EngineReadiness.SameModel("qwen3-vl:latest", "qwen3-vl"));
        Assert.True(EngineReadiness.SameModel("QWEN3-VL", "qwen3-vl:latest"));
        Assert.True(EngineReadiness.SameModel("qwen2.5vl:7b", " qwen2.5vl:7b "));
        // А разные теги — разные модели: «7b» и «72b» отличаются не записью.
        Assert.False(EngineReadiness.SameModel("qwen2.5vl:7b", "qwen2.5vl:72b"));
        Assert.False(EngineReadiness.SameModel("qwen2.5vl:7b", "qwen3-vl:7b"));
    }

    [Fact]
    public void AdviceFrom_ReadsReplacementOutOfGoogleRefusal()
    {
        // Дословный ответ Google (2026-08-20). Замена названа прямо в тексте отказа — если её не
        // вытащить, пользователь получит «модель недоступна» и никакой подсказки, на что менять.
        const string body = """
            {"error":{"code":404,"message":"gemini-2.5-flash-lite is not found for API version v1beta.
            Please update your code to use models/gemini-3.5-flash-lite for the latest features and improvements.",
            "status":"NOT_FOUND"}}
            """;
        Assert.Equal("поставщик рекомендует gemini-3.5-flash-lite", RecognitionModelCatalog.AdviceFrom(body));
    }

    [Fact]
    public void AdviceFrom_SilentWhenNothingSuggested()
    {
        // Модель, которой никогда не было (опечатка в имени), заменой не сопровождается — и выдумывать
        // её нельзя.
        Assert.Null(RecognitionModelCatalog.AdviceFrom(
            """{"error":{"code":404,"message":"models/gemini-3.5-pro is not found.","status":"NOT_FOUND"}}"""));
    }
}
