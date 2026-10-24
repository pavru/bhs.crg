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
        var plain = v.Replace(" ", "").Replace(',', '.');
        if (decimal.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        var expression = new Arithmetic(plain);
        return expression.TryEvaluate(out var value)
            ? Math.Round(value, 2, MidpointRounding.AwayFromZero)
            : throw new GrandSmetaFormatException($"{attribute} — не число: «{v}»");
    }

    public static decimal? Number(string? value, string what)
    {
        if (value is null) return null;
        return decimal.TryParse(value.Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var d)
            ? d
            : throw new GrandSmetaFormatException($"{what} — не число: «{value}»");
    }

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
internal sealed class GrandSmetaFormatException(string message) : Exception(message);

/// <summary>Числа, скобки, + − × ÷ — разбор рекурсивным спуском в <c>decimal</c>. Переменных нет: формула со
/// ссылкой («Ф1*2») — не цена, а отказ позиции.</summary>
internal sealed class Arithmetic(string text)
{
    private int _at;

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
}
