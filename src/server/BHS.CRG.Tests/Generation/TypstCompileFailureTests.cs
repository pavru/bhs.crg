using BHS.CRG.Infrastructure.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Отказ компилятора шаблона — понятным текстом и без путей сервера (issue #1047).
///
/// <para>⚠️ Все образцы <c>stderr</c> ниже СНЯТЫ С ЖИВОГО Typst (<c>--diagnostic-format short</c>),
/// а не составлены по памяти. Разница принципиальная: компилятор печатает АБСОЛЮТНЫЙ путь — на
/// Windows ещё и с префиксом «\\?\» — там, где по виду аргумента ожидался относительный, а в случае
/// «file not found» кладёт полный путь ВНУТРЬ текста сообщения. На выдуманных образцах и разбор
/// адреса, и вычистка путей зеленели бы, а на живом выводе не сработали.</para>
///
/// <para>Typst в наборе не нужен и не запускается: разбор — чистая функция, ей на вход даётся
/// записанный вывод. Тест от наличия CLI не зависит и пропусков не имеет.</para>
/// </summary>
public class TypstCompileFailureTests
{
    /// <summary>Папка прогона из записанных образцов (та, в которой Typst их и напечатал).</summary>
    private const string RunDir = @"C:\Users\alex\AppData\Local\Temp\tmp.Lg1WPk04YT";

    private const string MissingKeyStdErr =
        @"\\?\C:\Users\alex\AppData\Local\Temp\tmp.Lg1WPk04YT\template.typ:8:24: error: dictionary does not contain key ""pageCount""";

    private const string MissingFileStdErr =
        @"\\?\C:\Users\alex\AppData\Local\Temp\tmp.Lg1WPk04YT\template.typ:2:7: error: file not found (searched at \\?\C:\Users\alex\AppData\Local\Temp\tmp.Lg1WPk04YT\assets\missing.png)";

    private const string LibraryStdErr =
        @"\\?\C:\Users\alex\AppData\Local\Temp\tmp.Lg1WPk04YT\userlib\helpers.typ:2:18: error: unknown variable: undefined_thing";

    [Fact]
    public void Missing_field_names_the_key_the_file_and_the_line()
    {
        var refusal = TypstCompileFailure.TryDescribe(MissingKeyStdErr, RunDir);

        Assert.NotNull(refusal);
        // Ровно то, ради чего issue заведена: поле названо, место названо, и это не «ошибка сервера».
        Assert.Contains("pageCount", refusal.Message);
        Assert.Contains("шаблон документа, строка 8", refusal.Message);
        Assert.Contains("не поломка сервера", refusal.Message);
    }

    [Theory]
    [InlineData(MissingKeyStdErr)]
    [InlineData(MissingFileStdErr)]
    [InlineData(LibraryStdErr)]
    public void No_server_path_reaches_the_user(string stderr)
    {
        var refusal = TypstCompileFailure.TryDescribe(stderr, RunDir);

        Assert.NotNull(refusal);
        // Текст доменного отказа уходит клиенту ДОСЛОВНО — значит ни папки прогона, ни её обломков.
        Assert.DoesNotContain("AppData", refusal.Message);
        Assert.DoesNotContain("Temp", refusal.Message);
        Assert.DoesNotContain(@"\\?\", refusal.Message);
        Assert.DoesNotContain("C:", refusal.Message);
    }

    [Fact]
    public void Path_inside_the_message_survives_as_a_relative_one()
    {
        var refusal = TypstCompileFailure.TryDescribe(MissingFileStdErr, RunDir);

        // Путь вычищается ДО папки прогона, а не целиком: «assets\missing.png» — это то, что
        // пользователь ищет в ассетах шаблона, и без него отказ не адресует ничего.
        Assert.Contains("missing.png", refusal!.Message);
        Assert.Contains("шаблон документа, строка 2", refusal.Message);
    }

    [Fact]
    public void A_path_from_outside_the_run_directory_is_hidden_too()
    {
        // Вторая линия: путь, пришедший НЕ из папки прогона — из окружения сервера (шрифты, кеш
        // пакетов). Вычистка папки его не тронет, и без отдельной проверки он уехал бы наружу.
        var stderr = @"\\?\" + RunDir + @"\template.typ:1:1: error: failed to load font at "
                     + @"\\?\C:\Windows\Fonts\arial.ttf";

        var refusal = TypstCompileFailure.TryDescribe(stderr, RunDir);

        Assert.NotNull(refusal);
        Assert.DoesNotContain("Windows", refusal.Message);
        Assert.DoesNotContain("arial.ttf", refusal.Message);
        Assert.Contains("failed to load font", refusal.Message);
    }

    [Fact]
    public void A_file_of_the_user_library_is_named_by_its_own_path()
    {
        var refusal = TypstCompileFailure.TryDescribe(LibraryStdErr, RunDir);

        // Дерево библиотеки заводит администратор, и путь внутри него — его собственный: имя файла
        // здесь адресует правку точнее любого нашего названия.
        Assert.Contains("userlib/helpers.typ, строка 2", refusal!.Message);
        Assert.Contains("unknown variable: undefined_thing", refusal.Message);
    }

    [Fact]
    public void Unparseable_output_is_not_our_refusal()
    {
        // Компилятор сказал нечто, чего мы не понимаем, — это про установку, а не про шаблон.
        // null здесь означает «бросай прежнее исключение»: 500 с идентификатором запроса и запись
        // в журнале. Подсунуть сюда свой текст было бы хуже — человеку нечего исправлять.
        Assert.Null(TypstCompileFailure.TryDescribe("error: failed to spawn process", RunDir));
        Assert.Null(TypstCompileFailure.TryDescribe(string.Empty, RunDir));
        Assert.Null(TypstCompileFailure.TryDescribe(null, RunDir));
    }

    [Fact]
    public void Warnings_alone_are_not_a_refusal()
    {
        var stderr = @"\\?\" + RunDir + @"\template.typ:3:1: warning: unnecessary import";

        Assert.Null(TypstCompileFailure.TryDescribe(stderr, RunDir));
    }

    [Fact]
    public void A_long_list_is_cut_and_says_so()
    {
        var lines = Enumerable.Range(1, 9)
            .Select(i => $@"\\?\{RunDir}\template.typ:{i}:1: error: unknown variable: v{i}");
        var refusal = TypstCompileFailure.TryDescribe(string.Join("\n", lines), RunDir);

        Assert.NotNull(refusal);
        Assert.Contains("строка 5", refusal.Message);
        Assert.DoesNotContain("строка 6", refusal.Message);
        // Урезание названо вслух: молчаливое «показали пять из девяти» читается как «их пять».
        Assert.Contains("и ещё 4", refusal.Message);
    }
}
