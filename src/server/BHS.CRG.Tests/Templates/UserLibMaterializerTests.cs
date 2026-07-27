using BHS.CRG.Application.Templates;
using BHS.CRG.Infrastructure.Generation;

namespace BHS.CRG.Tests.Templates;

/// <summary>
/// Раскладка библиотеки на диск (issue #473). Она общая для генерации, проверки при сохранении и
/// отладочного бандла — расхождение означало бы «проверка зелёная, генерация падает».
/// </summary>
public class UserLibMaterializerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "userlib-mat-" + Guid.NewGuid().ToString("N"));

    public UserLibMaterializerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EntrypointIsAtRoot_TreeIsInSubfolder()
    {
        await UserLibMaterializer.WriteAsync(_dir, "#import \"userlib/gost/f3.typ\": *",
            [new UserLibFile("gost/f3.typ", "#let place-f3() = []")]);

        // Именно так это видит шаблон: `#import "userlib.typ"` в корне (#353, дословно).
        Assert.True(File.Exists(Path.Combine(_dir, "userlib.typ")));
        Assert.True(File.Exists(Path.Combine(_dir, "userlib", "gost", "f3.typ")));
    }

    /// <summary>Файл обязан существовать всегда: иначе `#import "userlib.typ"` в шаблоне не резолвится.</summary>
    [Fact]
    public async Task EmptyLibrary_StillWritesEntrypoint()
    {
        await UserLibMaterializer.WriteAsync(_dir, null, null);
        Assert.Equal(UserLibMaterializer.EmptyEntrypoint,
            (await File.ReadAllTextAsync(Path.Combine(_dir, "userlib.typ"))).Trim());
    }

    /// <summary>
    /// Путь проверяется при сохранении, но записи мог наделать восстановленный бэкап из другой
    /// инсталляции. Запись за пределы дерева — не та ошибка, которую ловят один раз.
    /// </summary>
    [Fact]
    public async Task PathEscapingTheTree_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UserLibMaterializer.WriteAsync(_dir, "x",
                [new UserLibFile("../../evil.typ", "#let boom() = []")]));

        Assert.Contains("выходит за пределы", ex.Message);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.typ")));
    }
}
