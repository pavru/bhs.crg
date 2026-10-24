using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>
/// Открытый файл ГРАНД-Сметы: вид, версия программы и редакция схемы — до разбора (ТЗ PLAN-7, STG-12).
///
/// <para>На вход — <c>.gsfx</c> (ZIP: <c>Properties.txt</c>, <c>Data.xml</c>, <c>Data.sign</c>, у ВОР ещё
/// <c>Attachments/…</c>) или голый <c>Data.xml</c>; модель у них одна. Кодировка — из пролога (программа
/// пишет windows-1251), без пролога — windows-1251. Вложения ZIP не читаются: в модель идут только ссылки и
/// имена из XML.</para>
///
/// <para>Ловушка: <c>.gsfx</c>, открытый в самой программе, файловым API без общего доступа не читается —
/// поток открывают с <c>FileShare.ReadWrite</c> (это забота того, кто даёт поток).</para>
/// </summary>
public sealed class GrandSmetaFile
{
    /// <summary>Потолок распакованного <c>Data.xml</c>: крупнейший образец — 0,9 МБ, смета в тысячи строк —
    /// единицы МБ. Больше — не смета, а ZIP-бомба.</summary>
    private const long MaxXmlBytes = 64L * 1024 * 1024;

    private static readonly Dictionary<string, GrandSmetaKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{2B0470FD-477C-4359-9F34-EEBE36B7D340}"] = GrandSmetaKind.LocalEstimate,
        ["{2B0470FD-477C-4359-9F34-EEBE36B7D350}"] = GrandSmetaKind.VolumeStatement,
        ["{2B0470FD-477C-4359-9F34-EEBE36B7D354}"] = GrandSmetaKind.Conjuncture,
    };

    static GrandSmetaFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private GrandSmetaFile(XDocument document, GrandSmetaKind kind, string programVersion, GrandSmetaSchema schema)
    {
        Document = document;
        Kind = kind;
        ProgramVersion = programVersion;
        Schema = schema;
    }

    public GrandSmetaKind Kind { get; }

    /// <summary>Версия программы, записавшей файл (<c>Document/@ProgramVersion</c>, «16.2.2.59452»).</summary>
    public string ProgramVersion { get; }

    public GrandSmetaSchema Schema { get; }

    /// <summary>Документ с номерами строк — для перечня не разобранного.</summary>
    internal XDocument Document { get; }

    internal XElement Root => Document.Root!;

    public GrandSmetaSource Source => new(Kind, ProgramVersion, Schema.Edition);

    public static GrandSmetaFile Open(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var xml = bytes is [(byte)'P', (byte)'K', ..] ? DataXmlOf(bytes) : bytes;

        XDocument document;
        try
        {
            document = Load(xml);
        }
        catch (XmlException ex)
        {
            throw Refuse(GrandSmetaRefusal.NotGrandSmeta,
                $"Файл не читается как документ ГРАНД-Сметы: строка {ex.LineNumber} — ошибка разметки XML.");
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "Document" || (string?)root.Attribute("Generator") != "GrandSmeta")
            throw Refuse(GrandSmetaRefusal.NotGrandSmeta,
                "Это не документ ГРАНД-Сметы: нет корня Document с Generator=\"GrandSmeta\".");
        var version = (string?)root.Attribute("ProgramVersion");
        if (string.IsNullOrWhiteSpace(version))
            throw Refuse(GrandSmetaRefusal.NotGrandSmeta, "В документе ГРАНД-Сметы не записана версия программы.");
        var guid = (string?)root.Attribute("DocumentType") ?? "";
        if (!Kinds.TryGetValue(guid, out var kind))
            throw Refuse(GrandSmetaRefusal.UnknownKind,
                $"Вид документа ГРАНД-Сметы {guid} не читается: читаются локальный сметный расчёт, " +
                "ведомость объёмов работ и конъюнктурный анализ.");

        return new GrandSmetaFile(document, kind, version, GrandSmetaSchemas.Resolve(kind, version));
    }

    /// <summary>Отказ, если файл не того вида, что ждёт читатель.</summary>
    internal void Expect(GrandSmetaKind kind)
    {
        if (Kind != kind)
            throw Refuse(GrandSmetaRefusal.WrongKind,
                $"Ожидался {GrandSmetaSchemas.KindName(kind)}, а в файле — {GrandSmetaSchemas.KindName(Kind)}.");
    }

    private static byte[] DataXmlOf(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("Data.xml", StringComparison.OrdinalIgnoreCase))
                ?? throw Refuse(GrandSmetaRefusal.NotGrandSmeta, "В архиве нет Data.xml — это не файл ГРАНД-Сметы.");
            if (entry.Length > MaxXmlBytes)
                throw Refuse(GrandSmetaRefusal.NotGrandSmeta,
                    $"Data.xml в архиве больше {MaxXmlBytes / 1024 / 1024} МБ — это не смета.");
            using var data = entry.Open();
            using var copy = new MemoryStream();
            data.CopyTo(copy);
            return copy.ToArray();
        }
        catch (InvalidDataException)
        {
            throw Refuse(GrandSmetaRefusal.NotGrandSmeta, "Архив повреждён — файл ГРАНД-Сметы не читается.");
        }
    }

    private static XDocument Load(byte[] xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        // Без пролога XmlReader считает текст UTF-8, а программа пишет windows-1251.
        var head = Encoding.ASCII.GetString(xml, 0, Math.Min(xml.Length, 200));
        using var reader = head.Contains("encoding=", StringComparison.Ordinal) || xml is [0xEF, 0xBB, 0xBF, ..]
            ? XmlReader.Create(new MemoryStream(xml), settings)
            : XmlReader.Create(new StreamReader(new MemoryStream(xml), Encoding.GetEncoding(1251)), settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    internal static GrandSmetaRefusedException Refuse(GrandSmetaRefusal reason, string message) => new(reason, message);

    /// <summary>Разбор документа читателем: нечитаемое значение вне позиции (реквизит, итог раздела) — отказ
    /// документа с причиной, а не чужое исключение. Значения позиций ловит сам читатель — позиция уходит в не
    /// разобранные.</summary>
    internal static T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (GrandSmetaFormatException ex)
        {
            throw Refuse(GrandSmetaRefusal.DocumentInvalid, "В документе не читается значение вне позиций: " + ex.Message + ".");
        }
    }
}

/// <summary>Откуда модель: вид, версия программы и редакция схемы — импорт кладёт их рядом с файлом (STG-12).</summary>
public sealed record GrandSmetaSource(GrandSmetaKind Kind, string ProgramVersion, string SchemaEdition);

/// <summary>Не разобранное — поимённо: где (раздел и позиция), строка XML и причина (PLAN-7). Частичный разбор —
/// не отказ: решает человек в импорте («280 из 300, 20 не разобраны ▸»).</summary>
public sealed record Unparsed(string Place, int Line, string Reason);
