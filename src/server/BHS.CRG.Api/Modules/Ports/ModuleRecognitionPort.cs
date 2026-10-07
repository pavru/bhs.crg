using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Распознавание для модулей (ТЗ CORE-Q6, issue #1077) — переходник к распознавателю ядра.
///
/// <para>Делает то же, что чтение счёта в наборах данных: профиль с правками администратора, один
/// вызов на файл, тот же запрос к модели. Разница — в конце пути. Набор данных хранит СЫРЬЁ и
/// показывает его человеку как есть, поэтому битая таблица там — пустой список, а шапка — ключи с
/// пустыми значениями. Модуль раскладывает ответ по полям своей записи, и для него пустота под видом
/// ответа — отказ, переодетый в результат. Поэтому здесь «ничего не прочитано» — исключение, а
/// «таблица не разобрана» — названное поле результата.</para>
///
/// <para>⚠️ Чей профиль, порт не проверяет и проверить не может: кто его вызвал, контейнеру
/// неизвестно (изоляции модулей в процессе нет). Достаточно ворот каталога — профиль выключенного
/// модуля отвечает отказом с его названием.</para>
/// </summary>
public sealed class ModuleRecognitionPort(
    RecognitionProfileCatalog catalog,
    IRecognitionProfileProvider profiles,
    IDocumentRecognizer recognizer,
    IRecognitionPreflight preflight) : IModuleRecognition
{
    /// <summary>
    /// Виды, которые читаются ОДНИМ вызовом на файл целиком, и их запрос к модели. Остальные виды
    /// (штамп, обложка, таблица) читаются постранично или по группам листов — это конвейер наборов
    /// данных, и модулю он через этот порт не отдаётся. Новый вид вписывают сюда вместе с первым
    /// модулем, которому он нужен.
    /// </summary>
    private static readonly Dictionary<RecognitionProfileKind, Func<IReadOnlyList<RecognitionField>, string>> Prompts = new()
    {
        [RecognitionProfileKind.Invoice] = RecognitionShared.BuildInvoicePrompt,
    };

    public async Task EnsureReadyAsync(string profileCode, CancellationToken ct = default)
    {
        Declared(profileCode);
        // Текст причины — наш: его пишет предполётная проверка ядра, для человека. Это поле записи
        // RecognitionBlock, а не сообщение чужого исключения.
        if (await preflight.CheckAsync(ct) is { Message: var why })
            throw new RecognitionRefusedException(RecognitionRefusal.NotConfigured, why);
    }

    public async Task<ModuleRecognitionResult> RecognizeAsync(
        string profileCode, byte[] content, string mimeType, CancellationToken ct = default)
    {
        var declaration = Declared(profileCode);
        var profile = await profiles.GetDefaultAsync(declaration.Kind, ct);
        var fields = profile.ToRecognitionFields();
        var columns = profile.ToRowColumns();

        RecognitionResult answer;
        try
        {
            answer = await recognizer.RecognizeAsync(
                content, mimeType, RecognitionKinds.ComposeCallFields(profile), Prompts[declaration.Kind], ct);
        }
        catch (RecognitionSilentException ex)
        {
            throw new RecognitionRefusedException(RecognitionRefusal.NoAnswer,
                $"Модель не отдала ответ: {EngineRefusal.TextOf(ex)}", ex);
        }
        catch (Exception ex) when (ex is RecognitionUnavailableException or RecognitionLimitException)
        {
            // «Некому» и «не справились» цепочка бросает одним типом. Различает их предполётная
            // проверка — и спрашиваем её только здесь, на пути отказа: на удачном пути второй выбор
            // движка был бы платой ни за что.
            if (await preflight.CheckAsync(ct) is { Message: var why })
                throw new RecognitionRefusedException(RecognitionRefusal.NotConfigured, why, ex);
            throw new RecognitionRefusedException(RecognitionRefusal.Unavailable,
                $"Распознавание недоступно: {EngineRefusal.TextOf(ex)}", ex);
        }

        var values = fields.ToDictionary(f => f.Path, f => Blank(answer.Values.GetValueOrDefault(f.Path)));
        var rowsKey = RecognitionKinds.Describe(declaration.Kind).RowsKey;
        var (rows, rowsProblem) = rowsKey is null
            ? ([], null)
            : ReadRows(answer.Values.GetValueOrDefault(rowsKey), [.. columns.Select(c => c.Path)]);

        // Ни одного значения и ни одной строки — это не «пустой документ», а «не прочитали». Отдай мы
        // это результатом, получатель завёл бы запись из пустых полей — и она выглядела бы распознанной.
        if (values.Values.All(v => v is null) && rows.Count == 0)
            throw new RecognitionRefusedException(RecognitionRefusal.NoAnswer,
                "В ответе модели нет ни одного значения: документ не прочитан. Возможно, это не " +
                $"«{profile.Name}» или скан нечитаем.");

        return new ModuleRecognitionResult(
            [.. fields.Select(f => f.Path)], values, [.. columns.Select(c => c.Path)], rows, rowsProblem,
            answer.Engine);
    }

    /// <summary>
    /// Профиль по коду — с воротами. Неизвестный код или вид, который одним вызовом не читается, —
    /// ошибка модуля, а не человека: отказ не доменный, и в ответ уйдут общие слова.
    /// </summary>
    private RecognitionProfileDeclaration Declared(string profileCode)
    {
        var declaration = catalog.Find(profileCode) ?? throw new InvalidOperationException(
            $"Профиль распознавания «{profileCode}» не объявлен ни одним модулем сборки.");
        if (!Prompts.ContainsKey(declaration.Kind))
            throw new InvalidOperationException(
                $"Профиль «{profileCode}» вида {declaration.Kind} одним вызовом на файл не читается: " +
                "порт распознавания для модулей такого вида не знает.");
        catalog.Require(declaration.Owner, $"Профиль распознавания «{declaration.Name}»");
        return declaration;
    }

    /// <summary>
    /// Таблица из поля-массива ответа. Три исхода, и все три разные: строки; пустая таблица
    /// (<c>[]</c> — модель посмотрела и строк не нашла); не разобрано — тогда причина словами.
    /// </summary>
    internal static (IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, string? Problem) ReadRows(
        string? json, IReadOnlyList<string> columns)
    {
        if (string.IsNullOrWhiteSpace(json)) return ([], "модель не вернула таблицу");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ([], "таблица в ответе модели — не список строк");

            var rows = new List<IReadOnlyDictionary<string, string?>>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var row = columns.ToDictionary(c => c, c => item.TryGetProperty(c, out var v) ? Blank(Text(v)) : null);
                // Строка без единого значения — не строка документа: модели дописывают такие в хвост.
                if (row.Values.Any(v => v is not null)) rows.Add(row);
            }
            return (rows, null);
        }
        catch (JsonException)
        {
            return ([], "таблицу в ответе модели не разобрать");
        }
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
