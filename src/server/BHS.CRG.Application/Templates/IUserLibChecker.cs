namespace BHS.CRG.Application.Templates;

/// <summary>
/// Ошибка компиляции библиотеки с указанием ФАЙЛА — в дереве ошибка может прилететь откуда угодно,
/// в том числе из файла, который сейчас не открыт.
/// </summary>
/// <param name="Path">Путь от <c>userlib/</c>, либо <c>userlib.typ</c> для точки входа.</param>
/// <param name="InBuild">
/// Входит ли файл в сборку библиотеки — то есть достижим ли он по импортам от точки входа. Различие
/// нужно, чтобы не утверждать неправду (issue #506): ошибка в подключённом файле останавливает
/// генерацию ВСЕХ документов, а ошибка в неподключённом — только тех шаблонов, которые импортируют
/// этот файл напрямую (так делает один из наших). Проверяем теперь и неподключённые: раньше Typst до
/// них не доходил, и панель молчала о сломанном файле вовсе.
/// </param>
public record UserLibError(string Path, int Line, int Column, string Message, bool InBuild = true);

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
    /// <summary>Собирается ли САМА библиотека. Сломанный неподключённый файл ей не мешает.</summary>
    public bool Ok => !Errors.Any(e => e.InBuild);

    public static UserLibCheckResult Empty => new([], []);
}

/// <summary>Проверка дерева библиотеки: компиляция зонда + разбор дерева без Typst.</summary>
public interface IUserLibChecker
{
    Task<UserLibCheckResult> CheckAsync(
        string entrypointContent, IReadOnlyList<UserLibFile> files, CancellationToken ct);
}
