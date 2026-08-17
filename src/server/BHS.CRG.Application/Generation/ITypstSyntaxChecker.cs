namespace BHS.CRG.Application.Generation;

/// <summary>Одна синтаксическая ошибка Typst. <paramref name="File"/> — путь внутри сборки блоков
/// (<c>typeblocks.typ</c> или <c>typeblocks/&lt;слаг&gt;.typ</c>): с расколом по файлам (issue #772)
/// строки в разных модулях нумеруются заново, и без файла координата неоднозначна.</summary>
public record TypstSyntaxError(string File, int Line, int Column, string Message);

/// <summary>
/// Синтакс-проверка собранных блоков через Typst CLI (issue #309, фаза 2). Компилирует harness,
/// который лишь ИМПОРТИРУЕТ агрегатор — тела функций (замыкания) НЕ вызываются, поэтому ленивые
/// семантические ошибки (unknown variable, доступ к полю) НЕ всплывают, а ловятся именно
/// синтаксические (битые скобки/токены из редактора). Реализация делит запуск процесса с генератором
/// (Application не шеллит напрямую). Бросает при невозможности запустить CLI — обрабатывает вызывающий.
/// </summary>
public interface ITypstSyntaxChecker
{
    Task<IReadOnlyList<TypstSyntaxError>> CheckAsync(
        IReadOnlyList<TypstBlockFile> files, CancellationToken ct);
}
