namespace BHS.CRG.Application.Generation;

/// <summary>Одна синтаксическая ошибка Typst с координатами внутри typeblocks.typ.</summary>
public record TypstSyntaxError(int Line, int Column, string Message);

/// <summary>
/// Синтакс-проверка собранного typeblocks.typ через Typst CLI (issue #309, фаза 2). Компилирует
/// harness, который лишь ИМПОРТИРУЕТ typeblocks.typ — тела функций (замыкания) НЕ вызываются, поэтому
/// ленивые семантические ошибки (unknown variable, доступ к полю) НЕ всплывают, а ловятся именно
/// синтаксические (битые скобки/токены из редактора). Реализация делит запуск процесса с генератором
/// (Application не шеллит напрямую). Бросает при невозможности запустить CLI — обрабатывает вызывающий.
/// </summary>
public interface ITypstSyntaxChecker
{
    Task<IReadOnlyList<TypstSyntaxError>> CheckAsync(string typeBlocksContent, CancellationToken ct);
}
