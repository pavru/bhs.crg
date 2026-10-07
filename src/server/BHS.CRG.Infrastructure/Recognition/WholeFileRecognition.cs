using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Документ, который читается ОДНИМ вызовом на файл целиком (сегодня — счёт): общий шаг для всех, кто
/// так читает (issue #1077, ревью PR #1254).
///
/// <para>Потребителей два — PDF-источник набора данных и порт распознавания для модулей. Пока шаг
/// был написан в каждом, они расходились: один и тот же отказ движка отвечал разными типами, а один
/// и тот же ответ модели разбирался двумя разборщиками с разным результатом. Один профиль и один
/// запрос обязаны давать одно и то же, кто бы ни спросил.</para>
/// </summary>
public static class WholeFileRecognition
{
    /// <summary>
    /// Профиль → поля вызова → запрос вида → ответ. Отказ движка — <see cref="RecognitionRefusedException" />
    /// с видом причины.
    /// </summary>
    /// <param name="preflight">
    /// Чем отличить «распознавать некому» от «не справились»: цепочка бросает их одним типом. Не
    /// передан — причина всегда «недоступно», текст при этом тот же.
    /// </param>
    public static async Task<RecognitionResult> RunAsync(
        IDocumentRecognizer recognizer, IRecognitionPreflight? preflight, ResolvedRecognitionProfile profile,
        byte[] content, string mimeType, CancellationToken ct)
    {
        var prompt = RecognitionKinds.Describe(profile.Kind).WholeFilePrompt ?? throw new InvalidOperationException(
            $"Вид {profile.Kind} одним вызовом на файл не читается: запроса для такого чтения у него нет.");
        try
        {
            return await recognizer.RecognizeAsync(
                content, mimeType, RecognitionKinds.ComposeCallFields(profile), prompt, ct);
        }
        catch (RecognitionSilentException ex)
        {
            // Отдельно от «недоступно» ради текста: движок работает, но ответа не отдал, и совет
            // проверять настройки был бы ложью.
            throw new RecognitionRefusedException(RecognitionRefusal.NoAnswer,
                $"Модель не отдала ответ: {EngineRefusal.TextOf(ex)}", ex);
        }
        catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
        {
            // Проверку спрашиваем только здесь, на пути отказа: на удачном пути второй выбор движка
            // был бы платой ни за что.
            if (await WhyNotConfiguredAsync(preflight, ct) is { } why)
                throw new RecognitionRefusedException(RecognitionRefusal.NotConfigured, why, ex);
            throw new RecognitionRefusedException(RecognitionRefusal.Unavailable,
                $"Распознавание недоступно: {EngineRefusal.TextOf(ex)}", ex)
            {
                RetryAfterSeconds = (ex as RecognitionLimitException)?.RetryAfterSeconds,
            };
        }
    }

    /// <summary>
    /// Текст причины «распознавать некому» — наш: его пишет предполётная проверка ядра, для человека
    /// (поле записи <see cref="RecognitionBlock" />, а не сообщение чужого исключения).
    ///
    /// ⚠️ Сама проверка ходит в настройки и может пойти к движку с пробой зрения — к тому самому,
    /// который только что отказал. Её сбой исходного отказа не затирает: причина остаётся
    /// «недоступно» со словами движка, а не чужое исключение с общими словами.
    /// </summary>
    private static async Task<string?> WhyNotConfiguredAsync(IRecognitionPreflight? preflight, CancellationToken ct)
    {
        if (preflight is null) return null;
        try
        {
            return await preflight.CheckAsync(ct) is { Message: var why } ? why : null;
        }
        catch (Exception probe) when (probe is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Таблица из поля-массива ответа — ЕДИНСТВЕННЫЙ разбор. Значения любого вида приводятся к
    /// тексту: модель пишет количество то строкой, то числом, и число не повод терять всю таблицу.
    ///
    /// <para>Исходов три, и все три разные: строки; пустая таблица (<c>[]</c> — модель посмотрела и
    /// строк не нашла); не разобрано — тогда <c>Problem</c> называет причину. Массив, из которого не
    /// вышло ни одной строки, — тоже «не разобрано»: модель что-то вернула, а читать нечего.</para>
    /// </summary>
    public static (List<Dictionary<string, string?>> Rows, string? Problem) ReadRows(string? json)
    {
        var rows = new List<Dictionary<string, string?>>();
        if (string.IsNullOrWhiteSpace(json)) return (rows, "модель не вернула таблицу");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (rows, "таблица в ответе модели — не список строк");

            var items = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                items++;
                if (item.ValueKind != JsonValueKind.Object) continue;
                rows.Add(item.EnumerateObject().ToDictionary(p => p.Name, p => Text(p.Value)));
            }
            return (rows, items > 0 && rows.Count == 0 ? "строки таблицы в ответе модели — не записи с колонками" : null);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // ArgumentException — повтор ключа в одной строке: тоже «не разобрать», а не падение.
            return ([], "таблицу в ответе модели не разобрать");
        }
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };
}
