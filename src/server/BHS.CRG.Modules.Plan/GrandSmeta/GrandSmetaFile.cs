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
/// пишет windows-1251), без пролога — windows-1251, если текст не правильный UTF-8. Вложения ZIP не читаются: в модель идут только ссылки и
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

    /// <summary>Потолок архива из потока без перемотки — его приходится держать в памяти целиком. Вложения ВОР
    /// программа ограничивает 10 МБ (документы — 50 МБ каждый).</summary>
    private const long MaxArchiveBytes = 256L * 1024 * 1024;

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
        // Архив читается с места, если поток это позволяет: в .gsfx ВОР лежат вложения — их в память не тянем
        using var source = stream.CanSeek ? null : Bounded(stream, MaxArchiveBytes,
            $"Файл больше {MaxArchiveBytes / 1024 / 1024} МБ — это не файл ГРАНД-Сметы.");
        var input = source ?? stream;
        var start = input.Position;
        Span<byte> magic = stackalloc byte[2];
        var isZip = input.ReadAtLeast(magic, 2, throwOnEndOfStream: false) == 2 && magic is [(byte)'P', (byte)'K'];
        input.Position = start;
        using var xml = isZip ? DataXmlOf(input) : Bounded(input, MaxXmlBytes, TooLarge);

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

    private static string TooLarge => $"Data.xml больше {MaxXmlBytes / 1024 / 1024} МБ — это не смета.";

    private static MemoryStream DataXmlOf(Stream archive)
    {
        try
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("Data.xml", StringComparison.OrdinalIgnoreCase))
                ?? throw Refuse(GrandSmetaRefusal.NotGrandSmeta, "В архиве нет Data.xml — это не файл ГРАНД-Сметы.");
            if (entry.Length > MaxXmlBytes) throw Refuse(GrandSmetaRefusal.NotGrandSmeta, TooLarge);
            // Размер из заголовка архива — слова архива: распакованное считается заново
            using var data = entry.Open();
            return Bounded(data, MaxXmlBytes, TooLarge);
        }
        catch (InvalidDataException)
        {
            throw Refuse(GrandSmetaRefusal.NotGrandSmeta, "Архив повреждён — файл ГРАНД-Сметы не читается.");
        }
    }

    /// <summary>Копия потока в память — не больше <paramref name="limit" /> байт; больше — отказ, а не память
    /// процесса.</summary>
    private static MemoryStream Bounded(Stream source, long limit, string tooLarge)
    {
        var copy = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk)) > 0)
        {
            if (copy.Length + read > limit) throw Refuse(GrandSmetaRefusal.NotGrandSmeta, tooLarge);
            copy.Write(chunk, 0, read);
        }

        copy.Position = 0;
        return copy;
    }

    private static XDocument Load(MemoryStream xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        var bytes = xml.GetBuffer().AsSpan(0, (int)xml.Length);
        var head = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, 200)]);
        using var reader = head.Contains("encoding=", StringComparison.Ordinal) || bytes is [0xEF, 0xBB, 0xBF, ..]
            ? XmlReader.Create(xml, settings)
            : XmlReader.Create(new StreamReader(xml, EncodingWithoutProlog(bytes)), settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    /// <summary>Кодировка текста без пролога и BOM. Программа пишет windows-1251, но тот же Data.xml, пересохранённый
    /// сторонним инструментом, бывает в UTF-8 — и прочитанный как 1251 он дал бы «разобрано всё» с испорченными
    /// названиями. Кириллица windows-1251 правильным UTF-8 не складывается, поэтому правильный UTF-8 — UTF-8.</summary>
    private static Encoding EncodingWithoutProlog(ReadOnlySpan<byte> bytes) =>
        System.Text.Unicode.Utf8.IsValid(bytes) ? new UTF8Encoding(false) : Encoding.GetEncoding(1251);

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
        catch (OverflowException)
        {
            throw Refuse(GrandSmetaRefusal.DocumentInvalid,
                "В документе не читается значение вне позиций: " + GrandSmetaFormatException.OutOfRange + ".");
        }
    }
}

/// <summary>Откуда модель: вид, версия программы и редакция схемы — импорт кладёт их рядом с файлом (STG-12).</summary>
public sealed record GrandSmetaSource(GrandSmetaKind Kind, string ProgramVersion, string SchemaEdition);

/// <summary>Не разобранное — поимённо: где (раздел и позиция), строка XML и причина (PLAN-7). Частичный разбор —
/// не отказ: решает человек в импорте («280 из 300, 20 не разобраны ▸»).</summary>
public sealed record Unparsed(string Place, int Line, string Reason);
