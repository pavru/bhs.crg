using System.Text.Json;

namespace BHS.CRG.Application.Generation;

public enum DiagnosticSeverity { Warning, Error }

/// <summary>
/// Одна проблема, найденная при проверке разрешения ссылок контекста генерации.
/// </summary>
public record ResolutionDiagnostic(DiagnosticSeverity Severity, string Path, string Message);

/// <summary>
/// Исключение, прерывающее генерацию при наличии ошибок разрешения ссылок.
/// </summary>
public class ResolutionValidationException(IReadOnlyList<ResolutionDiagnostic> diagnostics)
    : Exception("Обнаружены ошибки разрешения ссылок перед генерацией")
{
    public IReadOnlyList<ResolutionDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Сканирует уже разрешённый контекст и находит оставшиеся неразрешёнными $ref-объекты
/// (например, ссылку на удалённую запись каталога или экземпляр) — это ошибки.
/// </summary>
public static class ResolutionScanner
{
    public static void ScanLeftoverRefs(GenerationContext ctx, List<ResolutionDiagnostic> diagnostics)
    {
        foreach (var (key, value) in ctx.Data)
            if (value is JsonElement el)
                Walk(key, el, diagnostics);
    }

    private static void Walk(string path, JsonElement el, List<ResolutionDiagnostic> diagnostics)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("$ref", out var refProp))
                {
                    var kind = refProp.GetString();
                    var target = el.TryGetProperty("entryId", out var eid) ? eid.GetString()
                        : el.TryGetProperty("instanceId", out var iid) ? iid.GetString()
                        : null;
                    var kindHuman = kind switch
                    {
                        "document" or "instance" => "документ",
                        "catalog" => "запись каталога",
                        _ => $"объект типа «{kind}»",
                    };
                    diagnostics.Add(new ResolutionDiagnostic(
                        DiagnosticSeverity.Error, path,
                        $"Ссылка на {kindHuman}{(target is null ? "" : $" (id {target})")} не разрешена — целевая запись не найдена или удалена."));
                    return; // глубже не идём — внутренность ссылки не данные
                }
                foreach (var p in el.EnumerateObject())
                    Walk($"{path}.{p.Name}", p.Value, diagnostics);
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    Walk($"{path}[{i++}]", item, diagnostics);
                break;
        }
    }
}
