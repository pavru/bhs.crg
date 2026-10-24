using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>Чтение значений ГРАНД-Сметы: числа с запятой («1,03»), даты «дд.ММ.гггг». Числа — <c>decimal</c> в
/// инвариантной культуре: деньги и объёмы без двоичной погрешности.</summary>
internal static class GrandSmetaXml
{
    /// <summary>Текст атрибута; перенос строки внутри названия программа пишет как CR LF — читается как LF.</summary>
    public static string? Text(this XElement e, string attribute)
    {
        var v = (string?)e.Attribute(attribute);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim().Replace("\r\n", "\n");
    }

    public static decimal? Number(this XElement e, string attribute) => Number(e.Text(attribute), attribute);

    /// <summary>
    /// Цена, которую сметчик мог записать формулой: в КА отпускная цена с НДС бывает «7510.73*1,1». Программа
    /// вычисляет её и показывает до копейки (печатная форма — 8261,8) — так же читается и здесь. Формула —
    /// только числа, скобки и четыре действия; что-то иное — не число.
    /// </summary>
    public static decimal? Money(this XElement e, string attribute)
    {
        var v = e.Text(attribute);
        if (v is null) return null;
        var plain = Plain(v);
        if (TryParse(plain, out var d)) return d;
        return new Arithmetic(plain).TryEvaluate(out var value)
            ? Math.Round(value, 2, MidpointRounding.AwayFromZero)
            : throw new GrandSmetaFormatException($"{attribute} — не число: «{v}»");
    }

    public static decimal? Number(string? value, string what)
    {
        if (value is null) return null;
        return TryParse(Plain(value), out var d) ? d : throw new GrandSmetaFormatException($"{what} — не число: «{value}»");
    }

    /// <summary>
    /// Округление до точности программы (<c>Quantity/@Precision</c>): от 7 знаков после запятой до «-3» —
    /// до тысяч. <see cref="Math.Round(decimal, int, MidpointRounding)" /> отрицательных знаков не принимает.
    /// </summary>
    public static decimal Round(decimal value, int digits)
    {
        if (digits >= 0) return Math.Round(value, Math.Min(digits, 28), MidpointRounding.AwayFromZero);
        var scale = 1m;
        for (var i = 0; i < Math.Min(-digits, 28); i++) scale *= 10;
        return Math.Round(value / scale, MidpointRounding.AwayFromZero) * scale;
    }

    /// <summary>Число без разделителей разрядов (пробел, неразрывный пробел) и с точкой вместо запятой.</summary>
    private static string Plain(string value) => value.Replace(" ", "").Replace("\u00A0", "").Replace(',', '.');

    private static bool TryParse(string plain, out decimal value) =>
        decimal.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static DateOnly? Date(this XElement e, string attribute)
    {
        var v = e.Text(attribute);
        if (v is null) return null;
        return DateOnly.TryParseExact(v, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new GrandSmetaFormatException($"{attribute} — не дата: «{v}»");
    }

    /// <summary>Дата создания: в атрибуте <c>СreationDate</c> первая буква — КИРИЛЛИЧЕСКАЯ «С», так пишет
    /// программа. Латинское написание читается тоже.</summary>
    public static DateOnly? CreationDate(this XElement docDates) =>
        docDates.Attribute("СreationDate") is not null ? docDates.Date("СreationDate") : docDates.Date("CreationDate");

    public static bool HasOption(this XElement e, string option, string attribute = "Options") =>
        e.Text(attribute)?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(option) == true;

    public static int Line(this XObject o) => o is IXmlLineInfo i && i.HasLineInfo() ? i.LineNumber : 0;

    public static IEnumerable<XElement> Chapters(this XElement root) => root.Elements("Chapters").Elements("Chapter");
}

/// <summary>Значение позиции не читается — позиция уходит в не разобранные с этой причиной, а не роняет
/// разбор документа.</summary>
internal sealed class GrandSmetaFormatException(string message) : Exception(message)
{
    /// <summary>Причина, когда числа файла читаются, а счёт по ним выходит за пределы <c>decimal</c>.</summary>
    public const string OutOfRange = "число вне допустимого диапазона";
}

/// <summary>Числа, скобки, + − × ÷ — разбор рекурсивным спуском в <c>decimal</c>. Переменных нет: формула со
/// ссылкой («Ф1*2») — не цена, а отказ позиции. Вложенность скобок и унарных минусов ограничена: файл —
/// недоверенный вход, а переполнение стека не перехватывается и роняет процесс целиком.</summary>
internal sealed class Arithmetic(string text)
{
    private const int MaxDepth = 64;

    private int _at;
    private int _depth;

    public bool TryEvaluate(out decimal value)
    {
        value = 0;
        try
        {
            value = Sum();
            return _at == text.Length;
        }
        catch (Exception ex) when (ex is FormatException or DivideByZeroException or OverflowException)
        {
            return false;
        }
    }

    private decimal Sum()
    {
        var v = Product();
        while (_at < text.Length && text[_at] is '+' or '-')
            v = text[_at++] == '+' ? v + Product() : v - Product();
        return v;
    }

    private decimal Product()
    {
        var v = Factor();
        while (_at < text.Length && text[_at] is '*' or '/')
            v = text[_at++] == '*' ? v * Factor() : v / Factor();
        return v;
    }

    private decimal Factor()
    {
        if (++_depth > MaxDepth) throw new FormatException();
        try
        {
            if (_at < text.Length && text[_at] == '-')
            {
                _at++;
                return -Factor();
            }

            if (_at < text.Length && text[_at] == '(')
            {
                _at++;
                var v = Sum();
                if (_at >= text.Length || text[_at++] != ')') throw new FormatException();
                return v;
            }

            var start = _at;
            while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.')) _at++;
            return decimal.Parse(text.AsSpan(start, _at - start), NumberStyles.Number, CultureInfo.InvariantCulture);
        }
        finally
        {
            _depth--;
        }
    }
}
