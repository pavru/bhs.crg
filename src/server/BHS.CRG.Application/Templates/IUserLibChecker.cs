namespace BHS.CRG.Application.Templates;

/// <summary>
/// Ошибка компиляции библиотеки с указанием ФАЙЛА — в дереве ошибка может прилететь откуда угодно,
/// в том числе из файла, который сейчас не открыт.
/// </summary>
/// <param name="Path">Путь от <c>userlib/</c>, либо <c>userlib.typ</c> для точки входа.</param>
public record UserLibError(string Path, int Line, int Column, string Message);

/// <summary>
/// Итог проверки библиотеки. Ошибки означают, что библиотека НЕ собирается — а её импортирует каждый
/// шаблон, поэтому встанет генерация всех документов. Предупреждения собираться не мешают.
/// </summary>
/// <remarks>
/// Проверка компилирует зонд, который лишь ИМПОРТИРУЕТ точку входа: ловятся синтаксис и битые пути
/// импортов, но НЕ поведение — шаблон, зовущий функцию с изменившейся сигнатурой, проверку пройдёт и
/// упадёт при генерации. Поэтому состояние называется «библиотека собирается», а не «шаблоны
/// работают»: второе было бы обещанием, которого проверка не даёт.
/// </remarks>
public record UserLibCheckResult(
    IReadOnlyList<UserLibError> Errors,
    IReadOnlyList<UserLibWarning> Warnings)
{
    public bool Ok => Errors.Count == 0;

    public static UserLibCheckResult Empty => new([], []);
}

/// <summary>Проверка дерева библиотеки: компиляция зонда + разбор дерева без Typst.</summary>
public interface IUserLibChecker
{
    Task<UserLibCheckResult> CheckAsync(
        string entrypointContent, IReadOnlyList<UserLibFile> files, CancellationToken ct);
}
