using BHS.CRG.Api.Common;
using Microsoft.AspNetCore.Http;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Что уходит клиенту при исключении (issue #691).
///
/// Проверка держится на одном различии: <b>наш</b> отказ доходит дословно, <b>чужой</b> — не
/// доходит вовсе. До этой правки признаком служил тип (<c>ArgumentException</c> → 400 с текстом),
/// а те же типы бросают Npgsql, SDK хранилища и Jint — и сообщение с хостом, базой или бакетом
/// уезжало пользователю. Отсюда и форма проверок: не «какой код вернули», а «попала ли строка
/// подключения в ответ».
/// </summary>
public class ApiErrorMappingTests
{
    private const string TraceId = "0HN7Q2K3J4L5M:00000003";

    /// Сообщения того же ВИДА, какой отдают чужие библиотеки; значения выдуманы — настоящих имён
    /// узлов и баз в тестовых данных быть не должно, репозиторий открытый.
    public static TheoryData<Exception> ForeignFailures() =>
    [
        new ArgumentException("Host=НЕЧТО;Database=НЕЧТО;Username=НЕЧТО;Password=НЕЧТО"),
        new KeyNotFoundException("The given key 'НЕЧТО' was not present in the dictionary."),
        new InvalidOperationException("Bucket 'НЕЧТО' at http://НЕЧТО:9000 is not accessible"),
        new UnauthorizedAccessException("Access to the path 'C:\\НЕЧТО\\НЕЧТО.xml' is denied."),
        new FormatException("An invalid IP address was specified: НЕЧТО"),
    ];

    [Theory]
    [MemberData(nameof(ForeignFailures))]
    public void ForeignException_TellsNothingAboutInternals(Exception ex)
    {
        var (status, message) = ApiErrorMapping.Describe(ex, TraceId);

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.DoesNotContain(ex.Message, message, StringComparison.Ordinal);
        // Не только текста целиком: маркером помечено всё, что в настоящем сообщении было бы именем
        // узла, базы, бакета или путём. Ни один не должен просочиться ни в каком виде.
        Assert.DoesNotContain("НЕЧТО", message, StringComparison.OrdinalIgnoreCase);
        // Ответ при этом не пустой: без идентификатора запроса администратору нечего искать в логе.
        Assert.Contains(TraceId, message, StringComparison.Ordinal);
    }

    public static TheoryData<DomainException, int> DomainRefusals() => new()
    {
        { new InvalidRequestException("Укажите название профиля."), StatusCodes.Status400BadRequest },
        { new NotFoundException("Профиль распознавания не найден."), StatusCodes.Status404NotFound },
        { new ConflictException("Тип документа с кодом «АОСР» уже существует."), StatusCodes.Status409Conflict },
        { new ForbiddenException("Действие доступно только администратору."), StatusCodes.Status403Forbidden },
    };

    [Theory]
    [MemberData(nameof(DomainRefusals))]
    public void DomainRefusal_ReachesUserVerbatim(DomainException ex, int expectedStatus)
    {
        var (status, message) = ApiErrorMapping.Describe(ex, TraceId);

        Assert.Equal(expectedStatus, status);
        Assert.Equal(ex.Message, message);
        // Идентификатор запроса тут лишний: пользователю сказано, что именно исправить.
        Assert.DoesNotContain(TraceId, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// «Не найдено» бросают чаще всего без слов. Раньше в этом случае наружу уходил английский текст
    /// фреймворка про отсутствующий ключ словаря.
    /// </summary>
    [Fact]
    public void NotFoundWithoutMessage_SaysSoInRussian()
    {
        var (status, message) = ApiErrorMapping.Describe(new NotFoundException(), TraceId);

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal("Запрошенный объект не найден.", message);
    }

    /// <summary>Слишком большое тело — своя ветка (issue #482), иначе английский текст фреймворка.</summary>
    [Fact]
    public void PayloadTooLarge_KeepsItsOwnAnswer()
    {
        var (status, message) = ApiErrorMapping.Describe(
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge), TraceId);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status);
        Assert.Equal("Запрос превышает допустимый размер.", message);
    }

    /// <summary>
    /// То же правило вне HTTP-ответа: запись фоновой задачи, уведомление в колокольчике, поле ошибки
    /// в журнале сверки. Выходов наружу больше одного, и правило у всех обязано быть общим — иначе
    /// закрытая дверь соседствует с открытой (через колокольчик уходил вывод компилятора шаблона).
    /// </summary>
    [Fact]
    public void RefusalText_IsOursVerbatim_AndForeignReplaced()
    {
        const string fallback = "Внутренняя ошибка. Задача 42 — подробности в журнале сервера.";

        Assert.Equal("Комплект не собран — соберите его перед отправкой.",
            Refusals.TextOr(new ConflictException("Комплект не собран — соберите его перед отправкой."), fallback));

        Assert.Equal(fallback,
            Refusals.TextOr(new InvalidOperationException("Typst compilation failed (exit 1):\nC:\\НЕЧТО\\template.typ:12"), fallback));
        Assert.Equal(fallback, Refusals.TextOr(null, fallback));
    }

    /// <summary>Исключения нет вовсе (обработчик вызван без причины) — молчать нельзя, но и сказать нечего.</summary>
    [Fact]
    public void NoException_StillAnswersGenerically()
    {
        var (status, message) = ApiErrorMapping.Describe(null, TraceId);

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Contains(TraceId, message, StringComparison.Ordinal);
    }
}
