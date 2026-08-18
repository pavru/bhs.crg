using System.Text;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Маска текста Typst: комментарии, строковые литералы и raw-блоки заменяются пробелами
/// ОДИН-В-ОДИН, длина сохраняется (issue #773).
///
/// <para><b>Зачем маска, а не вырезание.</b> Переписчик находит вхождения в очищенном тексте, а
/// заменять обязан в ОРИГИНАЛЕ; маска той же длины делает позиции общими для обоих, и ни один
/// символ вне найденных вхождений не двигается.</para>
///
/// <para><b>Кавычка — строка не всегда.</b> У Typst два режима: в code-режиме <c>"…"</c> это
/// строковый литерал, а в markup-режиме кавычка — обычный символ текста, и <c>\"</c> — способ его
/// написать. Наивная маска, гасящая всё от первой кавычки, на живом шаблоне АОСР приняла markup за
/// строку и погасила остаток файла: 13 настоящих вызовов из 27 стали «невидимы». Молча — то есть
/// переписчик бы их не тронул, а шаблон после миграции звал бы несуществующие имена.</para>
///
/// <para>Полный разбор режимов — это парсер Typst, которого здесь быть не должно. Работает
/// приближение: кавычка открывает литерал только там, где ожидается КОД — внутри круглых скобок
/// либо сразу после <c>: = + - * , ( { ;</c> или слова <c>import</c>/<c>include</c>. Промах в одну
/// сторону (приняли строку за markup) стоит лишнего кандидата, видного в отчёте; промах в другую
/// (приняли markup за строку) прячет вызов насовсем. При сомнении считаем, что это markup.</para>
/// </summary>
public static class TypstTextMask
{
    /// <summary>Что маска оставляет видимым.</summary>
    public enum Keep
    {
        /// <summary>Код без строк — для поиска вызовов функций.</summary>
        CodeOnly,

        /// <summary>ТОЛЬКО строковые литералы — для поиска путей к файлам. Путь вне литерала это
        /// проза документа: правка в ней меняет то, что человек увидит в PDF (issue #773).</summary>
        StringsOnly,
    }

    public static string Mask(string text, Keep keep = Keep.CodeOnly)
    {
        var sb = new StringBuilder(text.Length);
        var parenDepth = 0;
        var hideEverythingElse = keep == Keep.StringsOnly;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // «//» — комментарий и в markup (проверено на CLI: хвост строки в PDF не попадает), но
            // НЕ внутри схемы адреса: «https://…» Typst печатает целиком. Без этой оговорки строка
            // с голой ссылкой гасилась бы до конца, унося стоящие дальше вызовы.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/' && !(i > 0 && text[i - 1] == ':'))
            {
                while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                if (i < text.Length) sb.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < text.Length) { sb.Append("  "); i++; }   // закрывающие */
                continue;
            }

            if (c == '`')
            {
                // Raw-блок: ``` … ``` либо `…`. Внутри может быть что угодно, включая наши имена.
                var fence = 1;
                while (i + fence < text.Length && text[i + fence] == '`') fence++;
                var open = new string('`', fence);
                var end = text.IndexOf(open, i + fence, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + fence;
                for (int k = i; k < stop; k++) sb.Append(text[k] == '\n' ? '\n' : ' ');
                i = stop - 1;
                continue;
            }

            if (c == '"' && !IsEscaped(text, i) && LooksLikeCode(text, i, parenDepth))
            {
                var start = i;
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    i++;
                }
                var stop = Math.Min(i, text.Length - 1);
                var showString = keep == Keep.StringsOnly;
                for (int k = start; k <= stop; k++)
                    sb.Append(showString ? text[k] : (text[k] == '\n' ? '\n' : ' '));
                continue;
            }

            if (c == '(') parenDepth++;
            else if (c == ')' && parenDepth > 0) parenDepth--;
            // Пустая строка завершает выражение markup, поэтому незакрытая «(» в прозе не должна
            // навсегда переводить маску в режим кода: иначе кавычки дальше считались бы строками,
            // а вызовы внутри них исчезали из вида.
            else if (c == '\n' && i + 1 < text.Length && text[i + 1] == '\n') parenDepth = 0;

            sb.Append(hideEverythingElse ? (c == '\n' ? '\n' : ' ') : c);
        }
        return sb.ToString();
    }

    /// <summary>Экранированная кавычка — символ текста, а не начало литерала: именно на ней сорвалась
    /// прежняя маска. Считаем слэши перед ней: нечётное число означает, что экранирована кавычка.</summary>
    private static bool IsEscaped(string text, int i)
    {
        var slashes = 0;
        for (int k = i - 1; k >= 0 && text[k] == '\\'; k--) slashes++;
        return slashes % 2 == 1;
    }

    /// <summary>Ожидается ли здесь код: внутри круглых скобок либо сразу после символа/слова,
    /// за которым в Typst идёт выражение.</summary>
    private static bool LooksLikeCode(string text, int i, int parenDepth)
    {
        if (parenDepth > 0) return true;

        var k = i - 1;
        while (k >= 0 && (text[k] == ' ' || text[k] == '\t')) k--;
        if (k < 0) return false;

        // «[» намеренно НЕ в списке: за ним идёт content-блок, то есть markup, и кавычка в нём —
        // обычный символ. А «{» открывает code-блок, там литерал законен.
        if ("(:=+-*,{;".Contains(text[k])) return true;

        var end = k + 1;
        while (k >= 0 && (char.IsLetter(text[k]) || text[k] == '-')) k--;
        var word = text[(k + 1)..end];
        return word is "import" or "include";
    }
}
