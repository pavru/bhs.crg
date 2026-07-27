using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

/// <summary>
/// Файл дерева библиотеки Typst (issue #473). Путь — относительный, от папки <c>userlib/</c>
/// («gost/forms/f3.typ»); отдельной сущности у папок нет — папка существует ровно постольку,
/// поскольку в ней лежит файл, а пустая папка в Typst бессмысленна.
///
/// Точка входа <c>userlib.typ</c> сюда НЕ входит: она осталась в <see cref="TypstUserLib"/>, чтобы
/// уже выпущенные шаблоны с дословным <c>#import "userlib.typ": *</c> (#353) продолжали работать
/// без переписывания истории.
/// </summary>
public class TypstUserLibFile : Entity
{
    public string Path { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;

    private TypstUserLibFile() { }

    public static TypstUserLibFile Create(string path, string content)
        => new() { Path = path, Content = content };

    public void Update(string content) { Content = content; TouchUpdatedAt(); }

    public static TypstUserLibFile Restore(
        Guid id, string path, string content, DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new() { Id = id, Path = path, Content = content, CreatedAt = createdAt, UpdatedAt = updatedAt };
}
