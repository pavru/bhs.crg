namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Приложение к смете — файл ГРАНД-Сметы, который редакцией не становится (change <c>plan-11-conjuncture</c>,
/// решение В-7 (а)): конъюнктурный анализ (<see cref="ConjunctureAnalysis" />) и ведомость объёмов работ
/// (<see cref="VolumeStatement" />). Общее у них — файл источника с версиями, реквизиты файла и то, из какого
/// импорта приложение заведено; таблицы у каждого свои (общий родитель в модели EF не участвует).
///
/// <para>На план, «Ход работ» и график приложение не влияет: строк редакции оно не заводит и ни на что, кроме своей
/// сметы, не ссылается.</para>
/// </summary>
public abstract class EstimateAttachment
{
    public const int NumberLength = 100;
    public const int NameLength = 1000;

    public Guid Id { get; private protected set; }

    public Guid EstimateId { get; private set; }

    /// <summary>Импорт, из которого заведено, — идентификатором: импорт после принятия уходит, а повтор принятия
    /// по этому ключу находит заведённое (единственный индекс).</summary>
    public Guid ImportKey { get; private set; }

    /// <summary>Файл в хранилище модуля — путь, который вернул порт файлов; переходит от импорта.</summary>
    public string BlobPath { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public string? ProgramVersion { get; private set; }

    /// <summary>Редакция схемы, по которой файл разобран (ТЗ STG-12).</summary>
    public string? SchemaVersion { get; private set; }

    /// <summary>Номер документа из реквизитов файла.</summary>
    public string? Number { get; private set; }

    /// <summary>Название документа из реквизитов файла.</summary>
    public string? Name { get; private set; }

    public DateOnly? CreationDate { get; private set; }

    /// <summary>Разобрано позиций — «N» из «N из M»: принято могло быть и с не разобранными, с согласия.</summary>
    public int ParsedPositions { get; private set; }

    public int PositionsInFile { get; private set; }

    public DateTimeOffset ImportedAt { get; private set; }

    public Guid? ImportedBy { get; private set; }

    public string? ImportedByName { get; private set; }

    private protected void Fill(
        EstimateImport import, string? number, string? name, DateOnly? created, int parsed, int inFile,
        DateTimeOffset now, Guid? by, string? byName)
    {
        Id = Guid.NewGuid();
        EstimateId = import.EstimateId ?? throw new InvalidOperationException("Приложение заводится только к смете.");
        ImportKey = import.Id;
        BlobPath = import.BlobPath;
        FileName = import.FileName;
        ProgramVersion = import.ProgramVersion;
        SchemaVersion = import.SchemaVersion;
        Number = PlanText.Clip(number, NumberLength);
        Name = PlanText.Clip(name, NameLength);
        CreationDate = created;
        ParsedPositions = parsed;
        PositionsInFile = inFile;
        ImportedAt = now;
        ImportedBy = by;
        ImportedByName = byName;
    }
}
