using BHS.CRG.Application.Settings;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Одно правило «настроен ли движок» на всех потребителей (issue #797): цепочка распознавания,
/// веб-поиск и бейдж в настройках. Копий было две, и обе молчали — движок с галкой «включён», но без
/// ключа выпадал из перебора, а в настройках выглядел работающим.
/// </summary>
public class EngineReadinessTests
{
    [Fact]
    public void CloudRecognizer_NeedsKey()
    {
        Assert.Equal("не задан ключ",
            EngineReadiness.MissingForRecognition("Gemini", new IntegrationEngine { Enabled = true }));
        Assert.Null(EngineReadiness.MissingForRecognition("Gemini",
            new IntegrationEngine { Enabled = true, ApiKey = "k" }));
    }

    [Fact]
    public void Ollama_NeedsModel_NotKey()
    {
        // Локальная — ключа у неё нет вовсе, спрашивать его значило бы объявить её ненастроенной навсегда.
        Assert.Equal("не выбрана модель",
            EngineReadiness.MissingForRecognition("Ollama", new IntegrationEngine { Enabled = true, ApiKey = "k" }));
        Assert.Null(EngineReadiness.MissingForRecognition("Ollama",
            new IntegrationEngine { Enabled = true, Model = "qwen2.5vl:7b" }));
    }

    [Fact]
    public void Yandex_NamesEachMissingPart()
    {
        var e = new IntegrationEngine { Enabled = true };
        Assert.Equal("не заданы ключ и идентификатор каталога", EngineReadiness.MissingForWebSearch("Yandex", e));
        Assert.Equal("не задан идентификатор каталога",
            EngineReadiness.MissingForWebSearch("Yandex", new IntegrationEngine { Enabled = true, ApiKey = "k" }));
        Assert.Equal("не задан ключ",
            EngineReadiness.MissingForWebSearch("Yandex", new IntegrationEngine { Enabled = true, FolderId = "f" }));
        Assert.Null(EngineReadiness.MissingForWebSearch("Yandex",
            new IntegrationEngine { Enabled = true, ApiKey = "k", FolderId = "f" }));
    }

    [Fact]
    public void BlankKey_CountsAsMissing()
    {
        // Пробелы в поле ключа — это не ключ; иначе движок «настроен» и молча падает на первом запросе.
        Assert.Equal("не задан ключ",
            EngineReadiness.MissingForRecognition("Anthropic", new IntegrationEngine { Enabled = true, ApiKey = "   " }));
    }

    [Fact]
    public void Usable_RequiresBothEnabledAndConfigured()
    {
        var configured = new IntegrationEngine { Enabled = false, ApiKey = "k" };
        Assert.False(EngineReadiness.IsUsableForRecognition("Gemini", configured));   // выключен вручную — не жалуемся
        Assert.Null(EngineReadiness.MissingForRecognition("Gemini", configured));

        Assert.False(EngineReadiness.IsUsableForRecognition("Gemini", new IntegrationEngine { Enabled = true }));
        Assert.True(EngineReadiness.IsUsableForRecognition("Gemini", new IntegrationEngine { Enabled = true, ApiKey = "k" }));
    }
}
