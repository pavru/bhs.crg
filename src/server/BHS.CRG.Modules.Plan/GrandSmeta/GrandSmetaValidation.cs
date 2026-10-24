using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>
/// Проверка файла по редакции схемы (ТЗ PLAN-7): ошибка в позиции — позиция в перечень не разобранных с
/// номером строки и текстом ошибки, остальные разбираются; ошибка вне позиций — отказ всего разбора.
///
/// <para>Строгая: схема своя (<see cref="GrandSmetaSchemas" />) именно затем, чтобы не ослаблять проверку —
/// то, что программа пишет вопреки официальной схеме, разрешено правкой BHS-N поимённо, а не «пропускать
/// незнакомое».</para>
/// </summary>
internal sealed class GrandSmetaValidation
{
    private readonly Dictionary<XElement, (int Line, string Message)> _positions = [];

    private GrandSmetaValidation() { }

    public static GrandSmetaValidation Run(GrandSmetaFile file)
    {
        var result = new GrandSmetaValidation();
        var document = new List<(int Line, string Message)>();
        var schemas = GrandSmetaSchemas.Load(file.Schema);
        // Скомпилированная схема — одна на процесс, а потокобезопасность чтения XmlSchemaSet документация не
        // обещает; разбор идёт фоновой операцией импорта, очередь из двух файлов дешевле редкой гонки.
        lock (schemas)
        {
            file.Document.Validate(schemas, (sender, e) =>
            {
                var at = sender as XObject;
                var line = at is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : e.Exception?.LineNumber ?? 0;
                var position = PositionOf(at);
                if (position is not null)
                    result._positions.TryAdd(position, (line, e.Message));
                else
                    document.Add((line, e.Message));
            });
        }

        if (document.Count > 0)
            throw GrandSmetaFile.Refuse(GrandSmetaRefusal.DocumentInvalid,
                $"Файл не прошёл проверку по схеме {file.Schema.Edition} вне позиций: " +
                string.Join("; ", document.Take(3).Select(d => $"строка {d.Line}: {d.Message}")) +
                (document.Count > 3 ? $" (и ещё {document.Count - 3})" : "") + ".");
        return result;
    }

    /// <summary>
    /// Разбор одной позиции — у всех читателей одинаково: не прошла схему — в не разобранные с ошибкой схемы;
    /// значение не читается или число вне диапазона <c>decimal</c> — в не разобранные с причиной; иначе — в
    /// разобранные.
    /// </summary>
    public void Parse<T>(XElement position, string place, List<T> parsed, List<Unparsed> unparsed, Func<T> parse)
    {
        if (_positions.TryGetValue(position, out var error))
        {
            unparsed.Add(new Unparsed(place, error.Line, "не прошла проверку по схеме: " + error.Message));
            return;
        }

        try
        {
            parsed.Add(parse());
        }
        catch (GrandSmetaFormatException ex)
        {
            unparsed.Add(new Unparsed(place, position.Line(), ex.Message));
        }
        catch (OverflowException)
        {
            unparsed.Add(new Unparsed(place, position.Line(), GrandSmetaFormatException.OutOfRange));
        }
    }

    /// <summary>Позиция — самая внешняя <c>Position</c> раздела: у ЛСР в позиции бывают вложенные строки
    /// актов, у КА — предложения, и ошибка в них — ошибка позиции.</summary>
    private static XElement? PositionOf(XObject? at)
    {
        var element = at as XElement ?? at?.Parent;
        return element?.AncestorsAndSelf()
            .LastOrDefault(e => e.Name.LocalName == "Position" && e.Parent?.Name.LocalName == "Chapter");
    }
}
