using System.ComponentModel;
using System.Security.Claims;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using MediatR;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>Одна проблема разрешения ссылок в виде, пригодном для внешнего читателя.</summary>
/// <param name="Severity">Error / Warning. Error блокирует генерацию, Warning — нет.</param>
/// <param name="Path">Путь до проблемного места в реквизитах.</param>
/// <param name="Code">Вид проблемы: <c>missing-required</c>, <c>leftover-ref</c> и т.п.</param>
public record DocumentDiagnostic(string Severity, string Path, string Message, string Code);

/// <param name="Diagnostics">Пусто — документ проходит проверку.</param>
public record DocumentValidation(
    Guid DocumentId, int ErrorCount, int WarningCount, IReadOnlyList<DocumentDiagnostic> Diagnostics);

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

    private static DocumentDiagnostic ToDiagnostic(ResolutionDiagnostic d)
        => new(d.Severity.ToString(), d.Path, d.Message, d.Code);

    [McpServerTool(Name = "validate_document", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Проверка документа")]
    [Description("""
        Проверка документа средствами самой системы: незаполненные обязательные поля, битые ссылки,
        ошибки расчётных полей, циклы. Это детерминированная проверка, которую выполняет генерация, —
        цитируйте её вместо собственных догадок о том, что с документом не так.

        Error блокирует выпуск, Warning нет. Пустой список диагностик означает, что документ проходит.

        Только по одному документу: проверка прогоняет полный разбор с подстановкой наборов данных, и
        на комплекте в десятки документов это заняло бы слишком долго. Комплект обходите сами через
        get_document_set.
        """)]
    public async Task<DocumentValidation> ValidateDocumentAsync(
        [Description("Идентификатор документа.")] Guid documentId, CancellationToken ct)
    {
        var diagnostics = await mediator.Send(new ValidateInstanceResolutionQuery(documentId), ct);
        return new DocumentValidation(
            documentId,
            diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
            diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
            [.. diagnostics.Select(ToDiagnostic)]);
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
