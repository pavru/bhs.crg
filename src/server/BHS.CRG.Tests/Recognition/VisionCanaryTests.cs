using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Канарейка зрения (issue #801): движок обязан доказать, что картинка до него дошла.
///
/// Проверяется прежде всего НЕ «умеет ли предикат читать ответ», а обратное свойство: молчание,
/// мусор и отказ слепотой не считаются. Ложное «слепа» запрещает работу — оно дороже пропуска.
/// </summary>
public class VisionCanaryTests
{
    [Fact]
    public void SeesImage_AcceptsLiveAnswer()
    {
        // Дословный ответ qwen3-vl на эту самую картинку, замер 2026-08-20 (6,2 с): модель называет
        // пурпурную полосу розовой — за это отбирать зрение нельзя, спрашивают не про терминологию.
        Assert.True(VisionCanary.SeesImage("{\"colors\": [\"зеленый\", \"розовый\", \"желтый\"]}"));
    }

    [Theory]
    [InlineData("{\"colors\": [\"зелёный\", \"пурпурный\", \"жёлтый\"]}")]
    [InlineData("green, magenta, yellow")]
    [InlineData("```json\n{\"colors\":[\"Зелёная\",\"Фуксия\",\"Золотая\"]}\n```")]
    [InlineData("Слева зелёная полоса, в середине сиреневая, справа жёлтая.")]
    // Порядок вердиктом не проверяется: набор из трёх редких цветов наугад не составить, а зрячая
    // модель, перепутавшая две правые полосы, за это лишилась бы права работать.
    [InlineData("{\"colors\": [\"жёлтый\", \"зелёный\", \"пурпурный\"]}")]
    [InlineData("{\"colors\": [\"лаймовая\", \"пурпурная\", \"лимонная\"]}")]
    public void SeesImage_AcceptsSynonymsAndProse(string raw)
        => Assert.True(VisionCanary.SeesImage(raw));

    [Theory]
    [InlineData("{\"colors\": [\"зелёный\", \"жёлтый\"]}")]                  // одной полосы нет
    [InlineData("{\"colors\": [\"красный\", \"синий\", \"белый\"]}")]        // угадывание наугад
    [InlineData("Я не могу определить цвета на изображении.")]
    public void SeesImage_RejectsWrongAnswer(string raw)
        => Assert.False(VisionCanary.SeesImage(raw));

    [Fact]
    public void SeesImage_EmptyIsNotSighted()
    {
        // Пустой ответ — «не увидела» для предиката, но НЕ «слепа» для вердикта: разводит их
        // RecognitionModelCatalog. Замер 2026-08-20: модель без картинки думала 196 с и вернула пустоту.
        Assert.False(VisionCanary.SeesImage(""));
        Assert.False(VisionCanary.SeesImage(null));
    }

    [Fact]
    public void Prompt_DoesNotNameColors()
    {
        // Главное свойство промпта: он не подсказывает ответ. Назови он цвета — слепая модель
        // повторила бы их из вопроса, и канарейка выдала бы ей справку о зрении.
        var prompt = VisionCanary.BuildPrompt(VisionCanary.Fields).ToLowerInvariant();
        foreach (var color in new[] { "зелён", "зелен", "пурпур", "жёлт", "желт", "розов", "green", "magenta", "yellow" })
            Assert.DoesNotContain(color, prompt);
    }

    [Fact]
    public void Png_IsAValidImageAndStaysTheSame()
    {
        // Картинка — константа, и это часть проверки: подмена её на генерацию сделала бы «модель
        // слепа» неотличимым от «мы сгенерировали кривой PNG».
        Assert.Equal(174, VisionCanary.Png.Length);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], VisionCanary.Png[..4]);
    }

    [Fact]
    public void VisionIssue_SilentWhenEngineIsNotConfigured()
    {
        // У ненастроенного движка своя претензия («не выбрана модель»), и вторая, про зрение, только
        // сбивала бы: цепочка его и так не берёт.
        Assert.Null(EngineReadiness.VisionIssue("Ollama", new IntegrationEngine { Enabled = true },
            new VisionStatus(VisionState.Blind)));
    }

    [Fact]
    public void OnlyBlindIsAClaim_SightedAndUnknownAreNot()
    {
        // Тем же вопросом, каким спрашивает отбор движков (VisionIssue): второго способа задать его
        // заводить нельзя — разъехавшись, они дали бы зелёный тест на неиспользуемую ветку.
        var cfg = new IntegrationEngine { Enabled = true, Model = "gemma4:latest" };
        Assert.Null(EngineReadiness.VisionIssue("Ollama", cfg, VisionStatus.Sighted));
        // «Не проверено» работу не запрещает: иначе первый же таймаут канарейки отключил бы
        // распознавание, которое исправно работает.
        Assert.Null(EngineReadiness.VisionIssue("Ollama", cfg, VisionStatus.Unknown));
        Assert.Contains("не принимает изображения",
            EngineReadiness.VisionIssue("Ollama", cfg, new VisionStatus(VisionState.Blind))!);
    }
}
