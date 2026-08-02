using System.ComponentModel;
using System.Security.Claims;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using MediatR;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// Однотипные проблемы одной записью (issue #597).
///
/// Реестр материалов вернул 152 предупреждения, из которых 151 — дословно одна фраза «Поле
/// „Количество“: ожидается число, а хранится строка» с разными индексами строки: около 70 КБ ответа,
/// который потребитель первым делом сворачивал обратно в одну строку. Группировка — не украшение
/// вывода: это то, что с ним делают в любом случае.
/// </summary>
/// <param name="Severity">Error / Warning. Error блокирует генерацию, Warning — нет.</param>
/// <param name="Code">Вид проблемы: <c>missing-required</c>, <c>leftover-ref</c>, <c>value-type</c> и т.п.</param>
/// <param name="Count">Сколько раз проблема встретилась — по нему видно, это единичный случай или
/// системная беда всей таблицы.</param>
/// <param name="Paths">Адреса проблемных мест. По умолчанию несколько первых: путей столько же,
/// сколько проблем, и именно они составляли вес ответа.</param>
/// <param name="PathsTruncated">Адреса показаны не все. Признак обязателен: без него агент решит,
/// что проблем ровно столько, сколько путей, — а их <paramref name="Count"/>.</param>
public record DiagnosticGroup(
    string Severity, string Code, string Message, int Count,
    IReadOnlyList<string> Paths, bool PathsTruncated);

/// <param name="Diagnostics">Пусто — документ проходит проверку.</param>
/// <param name="DiagnosticCount">Сколько проблем всего — до группировки.</param>
public record DocumentValidation(
    Guid DocumentId, int ErrorCount, int WarningCount, int DiagnosticCount,
    IReadOnlyList<DiagnosticGroup> Diagnostics);

/// <param name="TemplateId">Шаблон, которым выпущен файл: на документ их может быть несколько.</param>
public record GeneratedFileInfo(Guid Id, string Format, Guid? TemplateId, DateTimeOffset GeneratedAt);

public record GenerationResult(Guid DocumentId, IReadOnlyList<GeneratedFileInfo> Files);

/// <summary>
/// Проверка и выпуск документа (issue #425) — переход от чистого чтения к оси ACT эпика #46.
/// Здесь живёт ЕДИНСТВЕННЫЙ записывающий инструмент MCP-сервера; всё остальное по-прежнему читает.
///
/// Слой тонкий: разбор аргументов и отправка команды через MediatR — ровно как в
/// <c>GenerationEndpoints</c>. HTTP-API и MCP остаются двумя адаптерами над одним ядром.
/// </summary>
[McpServerToolType]
public class DocumentActionTools(IMediator mediator, IHttpContextAccessor http)
{
    /// <summary>Агент действует ОТ ИМЕНИ пользователя — выпуск атрибутируется ему, а не «системе».</summary>
    private (Guid? Id, string? Name) CurrentUser
    {
        get
        {
            var user = http.HttpContext?.User;
            var raw = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
            return (Guid.TryParse(raw, out var id) ? id : null, user?.FindFirst("displayName")?.Value);
        }
    }

    /// <summary>Сколько адресов показывать в группе, пока не попросили все (issue #597).</summary>
    private const int SamplePathsPerGroup = 3;

    /// <summary>
    /// Свёртка одинаковых проблем в группы. Ключ группировки — (существенность, код, сообщение):
    /// сообщение входит в ключ, потому что один код носят проблемы с разным текстом («ожидается
    /// число» и «ожидается дата» — это <c>value-type</c> оба), а разный текст требует разного
    /// разбирательства.
    ///
    /// Порядок групп — по убыванию существенности, затем по числу вхождений: сверху то, что
    /// блокирует выпуск, и то, что случилось со всей таблицей.
    /// </summary>
    private static IReadOnlyList<DiagnosticGroup> Group(
        IEnumerable<ResolutionDiagnostic> diagnostics, bool allPaths) =>
        [.. diagnostics
            .GroupBy(d => (d.Severity, d.Code, d.Message))
            .Select(g =>
            {
                var paths = g.Select(d => d.Path).ToList();
                var shown = allPaths ? paths : paths.Take(SamplePathsPerGroup).ToList();
                return new DiagnosticGroup(
                    g.Key.Severity.ToString(), g.Key.Code, g.Key.Message, paths.Count,
                    shown, shown.Count < paths.Count);
            })
            .OrderByDescending(g => g.Severity == nameof(DiagnosticSeverity.Error))
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Message, StringComparer.OrdinalIgnoreCase)];

    [McpServerTool(Name = "validate_document", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Проверка документа")]
    [Description("""
        Проверка документа средствами самой системы: незаполненные обязательные поля, битые ссылки,
        ошибки расчётных полей, циклы. Это детерминированная проверка, которую выполняет генерация, —
        цитируйте её вместо собственных догадок о том, что с документом не так.

        Error блокирует выпуск, Warning нет. Пустой список диагностик означает, что документ проходит.

        Однотипные проблемы приходят ОДНОЙ записью: code, message, count и несколько адресов
        (paths). Строка таблицы с неверным типом значения даёт по проблеме на строку — их бывают
        сотни, и все с одинаковым текстом. Если pathsTruncated=true, адресов показано меньше, чем
        проблем; за полным перечнем вызовите с allPaths=true.

        Только по одному документу: проверка прогоняет полный разбор с подстановкой наборов данных, и
        на комплекте в десятки документов это заняло бы слишком долго. Комплект обходите сами через
        get_document_set.
        """)]
    public async Task<DocumentValidation> ValidateDocumentAsync(
        [Description("Идентификатор документа.")] Guid documentId,
        CancellationToken ct,
        [Description("""
            Показать адреса ВСЕХ проблем, а не первые несколько в каждой группе. Нужно, когда
            разбираете конкретные строки таблицы; на реестре материалов это десятки килобайт.
            """)] bool allPaths = false)
    {
        var diagnostics = await mediator.Send(new ValidateInstanceResolutionQuery(documentId), ct);
        return new DocumentValidation(
            documentId,
            diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
            diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
            diagnostics.Count,
            Group(diagnostics, allPaths));
    }

    [McpServerTool(Name = "generate_document", ReadOnly = false, Idempotent = true, Destructive = false,
        Title = "Выпустить документ (PDF)")]
    [Description("""
        Выпускает PDF документа по его шаблону. ЕДИНСТВЕННЫЙ инструмент, который меняет состояние
        системы: заменяет ранее выпущенные файлы этого же документа и переводит его в статус
        «Сгенерирован». Ничего за пределами документа не трогает.

        Если мешают ошибки разрешения ссылок, выпуск прерывается и возвращается их список — сначала
        проверьте документ через validate_document.
        """)]
    public async Task<GenerationResult> GenerateDocumentAsync(
        [Description("Идентификатор документа.")] Guid documentId, CancellationToken ct)
    {
        var (userId, userName) = CurrentUser;
        try
        {
            var files = await mediator.Send(
                new GenerateDocumentCommand(documentId, OutputFormat.Pdf, userName, userId), ct);
            return new GenerationResult(documentId,
                [.. files.Select(f => new GeneratedFileInfo(f.Id, f.Format.ToString(), f.TemplateId, f.CreatedAt))]);
        }
        catch (ResolutionValidationException ex)
        {
            // Голый отказ здесь бесполезен: агент должен узнать, ЧТО чинить, а не только что не вышло.
            var lines = ex.Diagnostics.Select(d => $"[{d.Severity}] {d.Path}: {d.Message}");
            throw new McpException(
                "Выпуск прерван из-за ошибок разрешения ссылок:\n" + string.Join("\n", lines));
        }
    }
}
