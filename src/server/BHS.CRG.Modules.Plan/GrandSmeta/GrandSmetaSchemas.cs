using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Schema;

namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>Вид документа ГРАНД-Сметы — по GUID <c>Document/@DocumentType</c>, а не по имени корня: корень у
/// всех один, <c>Document</c>.</summary>
public enum GrandSmetaKind
{
    /// <summary>Локальный сметный расчёт (<c>OpenXML_LS</c>).</summary>
    LocalEstimate,

    /// <summary>Ведомость объёмов работ (<c>OpenXML VOW</c>).</summary>
    VolumeStatement,

    /// <summary>Конъюнктурный анализ (<c>OpenXML_KA</c>).</summary>
    Conjuncture,
}

/// <summary>Редакция схемы, по которой проверяется файл: версия программы, записавшей его, соответствует ей
/// через <see cref="GrandSmetaSchemas" />.</summary>
public sealed record GrandSmetaSchema(GrandSmetaKind Kind, string Edition, string Resource);

/// <summary>
/// Один список «вид × версии программы → редакция схемы» (ТЗ STG-12). <b>Версии схемы в файле нет</b>: есть
/// только <c>@ProgramVersion</c>, а GUID вида у редакций один и тот же — поэтому редакцию называет этот список,
/// а не файл.
///
/// <para>Редакция — своя: официальная 2026.2.3 и правки BHS-N (<c>Schemas/*.xsd</c>, перечень — в
/// <c>docs/DEV_NOTES.md</c>). Официальная отвергает то, что пишет программа заказчика: 13 из 32 файлов.</para>
///
/// <para>Версия вне списка — отказ с версией и перечнем, а не попытка: новая версия программы могла
/// переименовать элемент, и разбор по старой схеме дал бы пустую колонку при «разобрано 300 из 300». Новая
/// версия — строка здесь (и правка схемы, если нужна) с образцом, отдельным маленьким PR.</para>
/// </summary>
public static class GrandSmetaSchemas
{
    public const string Edition = "2026.2.3-bhs.1";

    private static readonly GrandSmetaSchema Ls = new(GrandSmetaKind.LocalEstimate, Edition, "OpenXML_LS.xsd");
    private static readonly GrandSmetaSchema Vow = new(GrandSmetaKind.VolumeStatement, Edition, "OpenXML_VOW.xsd");
    private static readonly GrandSmetaSchema Ka = new(GrandSmetaKind.Conjuncture, Edition, "OpenXML_KA.xsd");

    /// <summary>Поддерживаемое — «главная.второстепенная» версии программы; по образцам заказчика 22–24.10.2026.</summary>
    private static readonly (GrandSmetaKind Kind, string Version, GrandSmetaSchema Schema)[] Supported =
    [
        (GrandSmetaKind.LocalEstimate, "16.2", Ls),
        (GrandSmetaKind.LocalEstimate, "16.3", Ls),
        (GrandSmetaKind.VolumeStatement, "15.1", Vow),
        (GrandSmetaKind.VolumeStatement, "15.3", Vow),
        (GrandSmetaKind.VolumeStatement, "16.2", Vow),
        (GrandSmetaKind.Conjuncture, "16.2", Ka),
    ];

    private static readonly ConcurrentDictionary<string, XmlSchemaSet> Compiled = new();

    /// <summary>Редакция для вида и версии; версия вне списка — <see cref="GrandSmetaRefusal.UnsupportedVersion" />.</summary>
    public static GrandSmetaSchema Resolve(GrandSmetaKind kind, string programVersion)
    {
        var parts = programVersion.Split('.');
        var line = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : programVersion;
        foreach (var s in Supported)
            if (s.Kind == kind && s.Version == line)
                return s.Schema;

        var known = string.Join(", ", Supported.Where(s => s.Kind == kind).Select(s => s.Version));
        throw new GrandSmetaRefusedException(GrandSmetaRefusal.UnsupportedVersion,
            $"Формат ГРАНД-Сметы версии {programVersion} ({KindName(kind)}) не поддерживается; поддерживаются: {known}.");
    }

    /// <summary>Скомпилированная схема — один раз на процесс: XSD локальной сметы — 430 КБ.</summary>
    public static XmlSchemaSet Load(GrandSmetaSchema schema) => Compiled.GetOrAdd(schema.Resource, resource =>
    {
        var name = $"GrandSmeta.Schemas.{resource}";
        using var stream = typeof(GrandSmetaSchemas).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Нет ресурса схемы {name}: сборка модуля собрана без XSD.");
        var set = new XmlSchemaSet();
        using var reader = XmlReader.Create(stream);
        set.Add(null, reader);
        set.Compile();
        return set;
    });

    public static string KindName(GrandSmetaKind kind) => kind switch
    {
        GrandSmetaKind.LocalEstimate => "локальный сметный расчёт",
        GrandSmetaKind.VolumeStatement => "ведомость объёмов работ",
        _ => "конъюнктурный анализ",
    };
}
