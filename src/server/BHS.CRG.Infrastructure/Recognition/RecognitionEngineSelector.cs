using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Recognition;

/// <param name="Ordered">Движки в порядке из настроек, годные к работе.</param>
/// <param name="Blind">Движки, отсеянные канарейкой: имя и претензия для человека.</param>
public record EngineSelection(
    IReadOnlyList<IRecognizerEngine> Ordered,
    IReadOnlyList<(string Engine, string Issue)> Blind);

/// <summary>
/// Кого из движков распознавания брать в работу — одно правило на всех спрашивающих (issue #801).
///
/// Спрашивают двое: цепочка (перед каждым вызовом) и предполётная проверка (перед постановкой
/// задачи). Разъехавшись, они дали бы худшее из возможного — задача, которую разрешили поставить и
/// тут же отказались выполнять, либо наоборот. Отбор жил в цепочке; вынесен сюда целиком, копий нет.
/// </summary>
public class RecognitionEngineSelector(
    IEnumerable<IRecognizerEngine> engines, IIntegrationSettings settings, IRecognitionModelCatalog catalog,
    ILogger<RecognitionEngineSelector> logger)
{
    /// <param name="probeVision">
    /// Можно ли ради ответа сходить к движку с канарейкой. Ответ кэшируется, поэтому постраничный
    /// прогон платит за пробу один раз; <c>false</c> оставлен для мест, где ждать нельзя вовсе.
    /// </param>
    public async Task<EngineSelection> SelectAsync(bool probeVision = true, CancellationToken ct = default)
    {
        var s = await settings.GetEffectiveAsync(ct);
        var byName = engines.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        // порядок из настроек; затем движки не упомянутые в порядке
        var order = s.RecognitionOrder.Count > 0 ? s.RecognitionOrder : ["Gemini", "Anthropic", "Ollama"];
        var ordered = order.Where(byName.ContainsKey)
            .Concat(byName.Keys.Where(n => !order.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Select(n => byName[n])
            .ToList();

        // Движок с галкой «включён», но без ключа/модели из перебора выпадает. Уровень Debug, а не
        // Warning, намеренно: распознавание вызывается ПОСТРАНИЧНО, и на альбоме в двести листов
        // предупреждение повторилось бы двести раз, ничего не добавив. Пользовательский сигнал об
        // этом — бейдж «не участвует» в настройках, он виден до запуска и не тонет в логе (#797).
        foreach (var e in ordered.Where(e => s.Rec(e.Name).Enabled))
            if (EngineReadiness.MissingForRecognition(e.Name, s.Rec(e.Name)) is { } missing)
                logger.LogDebug("Движок {Engine} включён, но не участвует: {Missing}", e.Name, missing);

        var configured = ordered.Where(e => EngineReadiness.IsUsableForRecognition(e.Name, s.Rec(e.Name))).ToList();

        var usable = new List<IRecognizerEngine>();
        var blind = new List<(string, string)>();
        foreach (var e in configured)
        {
            var cfg = s.Rec(e.Name);
            var vision = await catalog.GetVisionAsync(e.Name, cfg, cfg.Model ?? "",
                probeVision ? VisionProbe.IfUnknown : VisionProbe.CacheOnly, ct);
            if (EngineReadiness.VisionIssue(e.Name, cfg, vision) is { } issue)
            {
                // Debug по той же причине, что и выше: на альбоме это двести одинаковых строк.
                // Человеку про слепоту говорят до запуска — отказом и бейджем в настройках.
                logger.LogDebug("Движок {Engine} не участвует: {Issue}", e.Name, issue);
                blind.Add((e.Name, issue));
                continue;
            }
            usable.Add(e);
        }
        return new EngineSelection(usable, blind);
    }
}

/// <summary>
/// Предполётная проверка распознавания. Отдельный класс, а не метод цепочки, потому что вопросы
/// разные: цепочка спрашивает «кем распознать эту страницу», проверка — «есть ли вообще кому
/// поручить эти двести».
/// </summary>
public class RecognitionPreflight(RecognitionEngineSelector selector) : IRecognitionPreflight
{
    public async Task<RecognitionBlock?> CheckAsync(CancellationToken ct = default)
    {
        var selection = await selector.SelectAsync(probeVision: true, ct);
        if (selection.Ordered.Count > 0) return null;

        // Слепота названа отдельным кодом не ради полноты перечисления: интерфейсу нужно показать
        // разное. «Не настроено» чинится галкой и ключом, слепота — сменой модели, и совет
        // «проверьте настройки» во втором случае отправляет человека искать то, что и так на месте.
        if (selection.Blind.Count > 0)
            // С именем движка: претензия про модель, а движков с моделями может быть несколько, и
            // два одинаковых абзаца подряд не сказали бы, к какому из них идти.
            return new RecognitionBlock(RecognitionBlock.Blind,
                string.Join(" ", selection.Blind.Select(b => $"{b.Engine}: {b.Issue}")));

        return new RecognitionBlock(RecognitionBlock.NoEngine,
            "Нет включённых и настроенных движков распознавания. Проверьте «Настройки → Поиск и распознавание».");
    }
}
