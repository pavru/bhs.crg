using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Templates;

/// <summary>Библиотека целиком: точка входа плюс дерево.</summary>
public record UserLibSnapshot(string Entrypoint, IReadOnlyList<UserLibFile> Files)
{
    public static UserLibSnapshot Empty => new(string.Empty, []);

    /// <summary>Пустая библиотека — плейсхолдер вместо неё писать не нужно, это делает материализатор.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Entrypoint) && Files.Count == 0;
}

/// <summary>
/// Единая точка чтения библиотеки. До issue #473 три места (генерация, предпросмотр, отладочный
/// бандл) читали её каждое своим <c>GetAllAsync().FirstOrDefault()</c>; с появлением дерева такое
/// расхождение означало бы, что где-то файл подмешивается, а где-то нет — и разница вылезала бы
/// только в PDF.
/// </summary>
public interface IUserLibProvider
{
    Task<UserLibSnapshot> GetAsync(CancellationToken ct = default);
}

public class UserLibProvider(
    IRepository<TypstUserLib> libRepo, IRepository<TypstUserLibFile> fileRepo) : IUserLibProvider
{
    public async Task<UserLibSnapshot> GetAsync(CancellationToken ct = default)
    {
        var lib = (await libRepo.GetAllAsync(ct)).FirstOrDefault();
        var files = (await fileRepo.GetAllAsync(ct))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new UserLibFile(f.Path, f.Content))
            .ToList();

        return new UserLibSnapshot(lib?.Content ?? string.Empty, files);
    }
}
