using System.Text;
using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Собирает typeblocks.typ — функции рендеринга составных типов,
/// объявленные в схемах типов документов (свойство "typstRenders").
/// Используется и при генерации, и при выгрузке отладочного пакета,
/// чтобы оба пути давали идентичный typeblocks.typ.
/// </summary>
public static class TypstPreambleBuilder
{
    public static string Build(IEnumerable<DocumentType> compositeTypes)
    {
        var sb = new StringBuilder();
        foreach (var ct in compositeTypes)
        {
            if (!ct.Schema.RootElement.TryGetProperty("typstRenders", out var renders)) continue;
            if (renders.ValueKind != JsonValueKind.Array) continue;
            foreach (var render in renders.EnumerateArray())
            {
                var fnName = render.TryGetProperty("fnName", out var fn) ? fn.GetString() : null;
                var block = render.TryGetProperty("block", out var bl) ? bl.GetString() : null;
                if (string.IsNullOrWhiteSpace(fnName) || string.IsNullOrWhiteSpace(block)) continue;
                sb.AppendLine($"#let {fnName}(it) = {block}");
            }
        }
        return sb.ToString();
    }
}
