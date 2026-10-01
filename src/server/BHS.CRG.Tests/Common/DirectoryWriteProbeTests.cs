using BHS.CRG.Application.Common;

namespace BHS.CRG.Tests.Common;

/// <summary>
/// Проба записи в каталог (issue #1135): она обязана отличать «в каталог нельзя писать» от «в каталоге
/// сейчас пробует кто-то ещё». Со вторым она раньше путала первое — и роняла старт приложения с
/// советом сменить владельца каталога.
/// </summary>
public class DirectoryWriteProbeTests
{
    /// <summary>
    /// Пробы, идущие в одном каталоге разом, друг другу не мешают.
    ///
    /// <para>⚠️ Краснеет этот сторож только на Windows: там файл, открытый на запись, второму
    /// писателю не открыть. На Linux совместный доступ устроен мягче, и общая на всех проба
    /// проходила — поэтому в CI гонка не проявлялась, а у разработчика роняла случайный тест.</para>
    /// </summary>
    [Fact]
    public void Пробы_в_одном_каталоге_разом_друг_другу_не_мешают()
    {
        var dir = NewDirectory();
        try
        {
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
            {
                for (var i = 0; i < 100; i++) DirectoryWriteProbe.Run(dir);
            });

            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Годный каталог пробу проходит, и следа от неё не остаётся.</summary>
    [Fact]
    public void Проба_не_оставляет_за_собой_файла()
    {
        var dir = NewDirectory();
        try
        {
            DirectoryWriteProbe.Run(dir);
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Каталог, в который писать нельзя, пробу не проходит — исключением ввода-вывода, которое
    /// вызывающий переводит в названный отказ. Каталога здесь нет вовсе: запретить запись правами
    /// одинаково на Windows и Linux нечем.
    /// </summary>
    [Fact]
    public void Каталог_в_который_нельзя_писать_пробу_не_проходит()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"crg-probe-missing-{Guid.NewGuid():N}");

        Assert.ThrowsAny<IOException>(() => DirectoryWriteProbe.Run(missing));
        Assert.False(Directory.Exists(missing));
    }

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crg-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
