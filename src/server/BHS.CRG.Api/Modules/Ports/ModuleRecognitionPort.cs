using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Распознавание для модулей (ТЗ CORE-Q6, issue #1077) — переходник к распознавателю ядра.
///
/// <para>Читает тем же шагом, что и набор данных (<see cref="WholeFileRecognition" />): профиль с
/// правками администратора, один вызов на файл, тот же запрос, тот же разбор таблицы. Разница — в
/// конце пути. Набор данных хранит СЫРЬЁ и показывает его человеку как есть, поэтому битая таблица
/// там — пустой список, а шапка — ключи с пустыми значениями. Модуль раскладывает ответ по полям
/// своей записи, и для него пустота под видом ответа — отказ, переодетый в результат. Поэтому здесь
/// «ничего не прочитано» — исключение, а «таблица не разобрана» — названное поле результата.</para>
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
        string[] columns = [.. profile.ToRowColumns().Select(c => c.Path)];

        var answer = await WholeFileRecognition.RunAsync(recognizer, preflight, profile, content, mimeType, ct);

        var values = fields.ToDictionary(f => f.Path, f => Blank(answer.Values.GetValueOrDefault(f.Path)));
        var rowsKey = RecognitionKinds.Describe(declaration.Kind).RowsKey;
        var (rows, rowsProblem) = rowsKey is null
            ? ([], null)
            : Rows(answer.Values.GetValueOrDefault(rowsKey), columns);

        // Ни одного значения и ни одной строки — это не «пустой документ», а «не прочитали». Отдай мы
        // это результатом, получатель завёл бы запись из пустых полей — и она выглядела бы распознанной.
        if (values.Values.All(v => v is null) && rows.Count == 0)
            throw new RecognitionRefusedException(RecognitionRefusal.NoAnswer,
                "В ответе модели нет ни одного значения: документ не прочитан. Возможно, это не " +
                $"«{profile.Name}» или скан нечитаем.");

        return new ModuleRecognitionResult(
            [.. fields.Select(f => f.Path)], values, columns, rows, rowsProblem, answer.Engine);
    }

    /// <summary>
    /// Профиль по коду. Ворота — ПЕРВЫМИ: профиль выключенного модуля отвечает человеку отказом с
    /// названием модуля, каким бы ни был его вид. Неизвестный код и вид, который одним вызовом не
    /// читается, — ошибка модуля, а не человека: отказ не доменный, и в ответ уйдут общие слова.
    /// </summary>
    private RecognitionProfileDeclaration Declared(string profileCode)
    {
        var declaration = catalog.Find(profileCode) ?? throw new InvalidOperationException(
            $"Профиль распознавания «{profileCode}» не объявлен ни одним модулем сборки.");
        catalog.Require(declaration.Owner, $"Профиль распознавания «{declaration.Name}»");
        // Постраничные виды (штамп, обложка, таблица) — конвейер наборов данных, и модулю он через
        // этот порт не отдаётся. Какой вид читается одним вызовом, знает реестр видов.
        if (RecognitionKinds.Describe(declaration.Kind).WholeFilePrompt is null)
            throw new InvalidOperationException(
                $"Профиль «{profileCode}» вида {declaration.Kind} одним вызовом на файл не читается: " +
                "порт распознавания для модулей такого вида не знает.");
        return declaration;
    }

    /// <summary>
    /// Строки по колонкам профиля. Разбор общий; здесь — отбор запрошенного и то, чего сырью не
    /// нужно: строка без единого значения в запрошенных колонках строкой документа не считается
    /// (модели дописывают такие в хвост).
    ///
    /// <para>⚠️ Модель вернула записи, а ни одной строки не вышло — это «не разобрали», и оно
    /// названо: ключи пришли в другом написании или не те. Молчание здесь выглядело бы как «строк в
    /// документе нет», и получатель завёл бы запись без строк как прочитанную (ревью PR #1254).</para>
    /// </summary>
    private static (IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, string? Problem) Rows(
        string? json, IReadOnlyList<string> columns)
    {
        var (raw, problem) = WholeFileRecognition.ReadRows(json);
        var rows = raw
            .Select(r => columns.ToDictionary(c => c, c => Blank(r.GetValueOrDefault(c))))
            .Where(r => r.Values.Any(v => v is not null))
            .ToList<IReadOnlyDictionary<string, string?>>();

        if (problem is null && raw.Count > 0 && rows.Count == 0)
            problem = "в строках таблицы нет ни одной запрошенной колонки";
        return (rows, problem);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
