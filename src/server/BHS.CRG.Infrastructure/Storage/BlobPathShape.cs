using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BHS.CRG.Infrastructure.Storage;

/// <summary>
/// Как выглядит путь объекта, созданного приложением, — в одном месте (issue #672).
///
/// Заведено потому, что форма пути оказалась разъезжающимся знанием сразу в трёх местах: её строит
/// <see cref="MinIOBlobStorage" />, по ней опознаёт пути разовый сбор реестра, и её же подделывает
/// тестовое хранилище. Разъехались они не в теории: первая версия сбора искала форму, которую я
/// вывел из чтения кода, и на живой базе не нашла ни одного пути из полусотни — а тесты остались
/// зелёными, потому что подделка порождала ту же придуманную форму.
/// </summary>
public static partial class BlobPathShape
{
    /// <summary>Ключ объекта внутри бакета: <c>{дата}/{guid}_{имя файла}</c>.</summary>
    public static string NewObjectName(string fileName)
        => $"{DateTimeOffset.UtcNow:yyyy/MM/dd}/{Guid.NewGuid()}_{fileName}";

    /// <summary>
    /// Выражение, опознающее путь. Разделитель даты допускаем любой (<c>.</c>, <c>/</c>, <c>-</c>) —
    /// и это не перестраховка: <see cref="NewObjectName" /> форматирует дату как <c>yyyy/MM/dd</c>,
    /// где <c>/</c> в .NET означает не символ, а ПЛЕЙСХОЛДЕР разделителя даты текущей культуры. Под
    /// русской локалью путь выглядит <c>bhs-crg/2026.06.25/…</c>, под инвариантной — со слэшами.
    /// Одна и та же строка кода даёт разную раскладку хранилища на разных машинах.
    ///
    /// Синтаксис общий для POSIX-регулярных выражений Postgres и <c>System.Text.RegularExpressions</c>:
    /// выражение живёт здесь, а применяется и в SQL сбора, и в тесте, который сверяет его с тем, что
    /// на самом деле строит <see cref="NewObjectName" />.
    /// </summary>
    public const string Pattern = "^[^/]+/[0-9]{4}[./-][0-9]{2}[./-][0-9]{2}/[0-9a-fA-F-]{36}_";

    /// <summary>
    /// «Ключ» пути — дата и идентификатор, <c>ГГГГ/ММ/ДД/guid_</c>, — где угодно в тексте (issue #1094).
    ///
    /// <para>Нужен там, где о форме ХРАНЕНИЯ пути ничего не известно: в данных модулей. Путь может
    /// лежать массивом, JSON-ом в текстовой колонке, после адреса сервера, через разделитель с
    /// соседним — и вырезать его целиком оттуда нельзя: имя файла произвольно, и где оно кончается,
    /// по тексту не понять. Ключ же вырезается однозначно, а идентификатор в нём свой у каждого
    /// файла, так что ключ опознаёт файл не хуже полного пути.</para>
    ///
    /// <para>Необязательная обратная черта перед разделителем — JSON, записанный текстом: часть
    /// сериализаторов пишет <c>/</c> как <c>\/</c>. <see cref="KeyOf" /> её снимает.</para>
    /// </summary>
    public const string KeyInText = @"[0-9]{4}\\?[./-][0-9]{2}\\?[./-][0-9]{2}\\?/[0-9a-fA-F-]{36}_";

    /// <summary>Ключ пути или найденного в тексте вхождения; <c>null</c> — на путь не похоже.</summary>
    public static string? KeyOf(string text)
    {
        var match = KeyInTextRegex().Match(text);
        return match.Success ? match.Value.Replace("\\", "") : null;
    }

    [GeneratedRegex(KeyInText)]
    private static partial Regex KeyInTextRegex();

    /// <summary>Имя корзины S3 — буквы, цифры, точка, дефис — и сразу дата: перед путём нет ничего.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[0-9]{4}[./-][0-9]{2}[./-][0-9]{2}/[0-9a-fA-F-]{36}_")]
    private static partial Regex ExactPathRegex();

    /// <summary>Короче ключа пути (дата, разделитель, идентификатор, черта) файлом быть не может.</summary>
    public const int ShortestKey = 48;

    /// <summary>
    /// Что строка говорит о файлах: <c>null</c> — ключа пути в ней нет; иначе — точный ли это путь и
    /// какие ключи в ней найдены (issue #1261, #1422).
    ///
    /// <para><b>Точный путь</b> — строка, которую можно отдать хранилищу как есть: до даты в ней одно
    /// имя корзины и ничего больше, а ключ пути ровно один (ревью PR #1289). Общая форма
    /// (<see cref="Pattern" />) для этого слишком широка: «всё, что не слэш» перед датой принимало за
    /// корзину и начало JSON, и слова перед путём — текст целиком уходил в копию «путём», файла по нему
    /// не находилось, а предупреждения о пути внутри текста не было. Хвост после ключа проверить нечем
    /// — имя файла произвольно; второй ключ в строке выдаёт, что это уже не имя.</para>
    ///
    /// <para>Иначе ключи найдены <b>внутри текста</b> — JSON строкой, после адреса сервера: где в тексте
    /// кончается имя файла, не понять, и точного пути отсюда не взять. Ключи же вырезаются однозначно и
    /// опознают файл не хуже пути (<see cref="KeyInText" />).</para>
    ///
    /// <para>Одно правило на ядро и модули: копия ядра (<c>CoreFileScan</c>) и копия модулей
    /// (<c>ModuleFileScan</c>) не разойдутся в том, что считать путём.</para>
    /// </summary>
    public static BlobPathHit? Inspect(string text)
    {
        if (text.Length < ShortestKey || !MayHoldKey(text.AsSpan(), '/', '_', GuidChars)) return null;

        var matches = KeyInTextRegex().Matches(text);
        if (matches.Count == 0) return null;

        var keys = new string[matches.Count];
        for (var i = 0; i < keys.Length; i++) keys[i] = matches[i].Value.Replace("\\", "");
        return new BlobPathHit(keys.Length == 1 && ExactPathRegex().IsMatch(text), keys);
    }

    /// <summary>Точный ли это путь — в смысле <see cref="Inspect" />.</summary>
    public static bool IsExactPath(string text) => Inspect(text) is { Exact: true };

    /// <summary>
    /// Строки JSON-значения, в которых уместился бы ключ пути: само значение, элементы массива, поля
    /// объекта — на любой глубине (issue #1261).
    ///
    /// <para>Обход идёт по каждой копии, каждой оценке её веса и каждому восстановлению (ревью PR
    /// #1289), а в значениях лежат картинки в base64 — мегабайты на запись. Поэтому строка НЕ
    /// создаётся, пока по её сырым байтам не видно, что ключ в ней возможен: короткие значения —
    /// номера, даты, состояния — отсекает длина, длинные без «/идентификатор_» — отбор по байтам.</para>
    /// </summary>
    public static void VisitStrings(JsonElement value, Action<string> found)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                // Сырое значение — с кавычками и экранированием; в байтах оно не короче, чем в символах.
                var raw = JsonMarshal.GetRawUtf8Value(value);
                if (raw.Length - 2 >= ShortestKey && MayHoldKey(raw, (byte)'/', (byte)'_', GuidBytes))
                    found(value.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) VisitStrings(item, found);
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject()) VisitStrings(property.Value, found);
                break;
        }
    }

    /// <summary>
    /// Грубый отбор без выражения: где-то в тексте стоит «/», за ним 36 знаков идентификатора и «_».
    /// Знака «_» в алфавите base64 нет, поэтому картинка в base64 отбор не проходит, сколько бы «/» в
    /// ней ни было. Обратная черта перед «/» (JSON, записанный текстом) отбору не мешает: смотрим
    /// после «/». Один и тот же для строки и для сырых байтов JSON — разъехаться им нечем.
    /// </summary>
    private static bool MayHoldKey<T>(ReadOnlySpan<T> text, T slash, T underscore, SearchValues<T> guid)
        where T : IEquatable<T>
    {
        for (var from = 0; from < text.Length;)
        {
            var at = text[from..].IndexOf(slash);
            if (at < 0) return false;
            at += from;
            if (at + 37 < text.Length && text[at + 37].Equals(underscore) &&
                text.Slice(at + 1, 36).IndexOfAnyExcept(guid) < 0)
                return true;
            from = at + 1;
        }
        return false;
    }

    private const string GuidAlphabet = "0123456789abcdefABCDEF-";
    private static readonly SearchValues<char> GuidChars = SearchValues.Create(GuidAlphabet);
    private static readonly SearchValues<byte> GuidBytes = SearchValues.Create(System.Text.Encoding.ASCII.GetBytes(GuidAlphabet));

    /// <summary>
    /// Грубый предварительный отбор строк ДО разбора JSON: ищем «/ГГГГ&lt;разделитель&gt;» где угодно
    /// в тексте колонки. Нужен ради стоимости: без него сбор разворачивает каждый узел каждого
    /// JSONB-документа, а там лежат картинки в base64 — мегабайты на запись.
    /// </summary>
    public const string RoughPattern = "/[0-9]{4}[./-][0-9]{2}[./-][0-9]{2}/";
}

/// <summary>Строка, в которой найден ключ пути (<see cref="BlobPathShape.Inspect" />).</summary>
/// <param name="Exact">Строка и есть путь — её можно отдать хранилищу как есть.</param>
/// <param name="Keys">Ключи путей в строке (у точного пути — один), без обратных черт.</param>
public sealed record BlobPathHit(bool Exact, IReadOnlyList<string> Keys);
