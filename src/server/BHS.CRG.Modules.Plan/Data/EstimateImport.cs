using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>Состояние импорта сметы (change <c>plan-07-estimate-import</c>, ТЗ PLAN-7).</summary>
public enum EstimateImportState
{
    /// <summary>Файл принят, разбор идёт фоновой операцией.</summary>
    Parsing,

    /// <summary>Разобран: отчёт готов, решение за человеком.</summary>
    Parsed,

    /// <summary>Не разобран вовсе — причина в <see cref="EstimateImport.RefusalText" />; редакции из него нет.</summary>
    Rejected,

    /// <summary>Из импорта создана редакция (<see cref="EstimateImport.RevisionId" />).</summary>
    Committed,
}

/// <summary>
/// Запись импорта файла ГРАНД-Сметы (change <c>plan-07-estimate-import</c>, ТЗ PLAN-6, PLAN-7, D6 зонтика
/// <c>plan-00-epic</c>): между загрузкой файла и решением человека «создать редакцию».
///
/// <para><b>Модель разбора хранится у записи</b> (<see cref="Parsed" />, <see cref="Unparsed" />) до решения
/// человека: создание редакции не разбирает файл второй раз и не видит другого результата, чем тот, что
/// человек прочитал в отчёте.</para>
///
/// <para><b>Куда — выбирает человек при загрузке</b> (решение В-12 (а)): либо новая редакция сметы
/// <see cref="EstimateId" />, либо новая смета с названием, разделом и видом. Совпадение реквизитов файла с
/// прежней редакцией молча не проверяется — расхождение номера ЛСР показывается в отчёте пометкой.</para>
/// </summary>
public sealed class EstimateImport
{
    public const int FileNameLength = 300;
    public const int VersionLength = 50;
    public const int LsrNumberLength = 100;
    public const int LsrNameLength = 1000;
    public const int RefusalCodeLength = 32;

    /// <summary>Код отказа, когда разбор упал не на файле, а на сбое хранилища или базы.</summary>
    public const string FailedCode = "Failed";

    private EstimateImport() { }

    public Guid Id { get; private set; }

    /// <summary>Стройка — запись ядра, идентификатором (ТЗ CORE-34.1).</summary>
    public Guid ConstructionId { get; private set; }

    /// <summary>Смета, в которую ляжет новая редакция; <c>null</c> — импорт заводит новую смету.</summary>
    public Guid? EstimateId { get; private set; }

    /// <summary>Название новой сметы — только когда <see cref="EstimateId" /> не назван.</summary>
    public string? NewEstimateName { get; private set; }

    public Guid? NewEstimateSectionId { get; private set; }

    public EstimateKind? NewEstimateKind { get; private set; }

    /// <summary>Файл в хранилище модуля — путь, который вернул порт файлов: восстановить его нельзя.</summary>
    public string BlobPath { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public EstimateImportState State { get; private set; }

    /// <summary>Вид документа; <c>null</c> — файл ещё не открыт или не открылся вовсе.</summary>
    public GrandSmetaKind? Kind { get; private set; }

    /// <summary>Версия программы, записавшей файл («16.2.2.59452»).</summary>
    public string? ProgramVersion { get; private set; }

    /// <summary>Редакция схемы, по которой файл разобран (<c>GrandSmetaSchemas.Edition</c>, ТЗ STG-12).</summary>
    public string? SchemaVersion { get; private set; }

    /// <summary>Номер ЛСР из реквизитов файла — по нему отчёт сверяет файл с прежней редакцией сметы.</summary>
    public string? LsrNumber { get; private set; }

    public string? LsrName { get; private set; }

    /// <summary>Модель разбора ЛСР (<c>ParsedLocalEstimate</c> без не разобранного) — JSON; у ВОР и КА — пусто.</summary>
    public string? Parsed { get; private set; }

    /// <summary>Не разобранные позиции с причиной — JSON-массив; у ВОР и КА — пусто.</summary>
    public string? Unparsed { get; private set; }

    /// <summary>Почему не разобран: имя <c>GrandSmetaRefusal</c> или <see cref="FailedCode" />.</summary>
    public string? RefusalCode { get; private set; }

    public string? RefusalText { get; private set; }

    /// <summary>Фоновая операция разбора — по ней отчёт показывает ход.</summary>
    public Guid? JobId { get; private set; }

    /// <summary>Редакция, созданная из импорта (<c>plan-07</c>, PR 2).</summary>
    public Guid? RevisionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Кто загрузил — справка, а не связь.</summary>
    public Guid? CreatedBy { get; private set; }

    public string? CreatedByName { get; private set; }

    public DateTimeOffset? ParsedAt { get; private set; }

    /// <summary>Импорт в новую редакцию существующей сметы.</summary>
    public static EstimateImport IntoEstimate(
        Estimate estimate, string blobPath, string fileName, Guid? by, string? byName, DateTimeOffset now) =>
        New(estimate.ConstructionId, blobPath, fileName, by, byName, now, i => i.EstimateId = estimate.Id);

    /// <summary>Импорт, который заведёт новую смету: название проверено правилами сметы уже здесь.</summary>
    public static EstimateImport IntoNewEstimate(
        Guid constructionId, string name, Guid? sectionId, EstimateKind kind, string blobPath, string fileName,
        Guid? by, string? byName, DateTimeOffset now) =>
        New(constructionId, blobPath, fileName, by, byName, now, i =>
        {
            i.NewEstimateName = Estimate.CheckedName(name);
            i.NewEstimateSectionId = sectionId;
            i.NewEstimateKind = kind;
        });

    private static EstimateImport New(
        Guid constructionId, string blobPath, string fileName, Guid? by, string? byName, DateTimeOffset now,
        Action<EstimateImport> target)
    {
        var import = new EstimateImport
        {
            Id = Guid.NewGuid(),
            ConstructionId = constructionId,
            BlobPath = blobPath,
            FileName = PlanText.Clip(fileName, FileNameLength) ?? string.Empty,
            State = EstimateImportState.Parsing,
            CreatedAt = now,
            CreatedBy = by,
            CreatedByName = byName,
        };
        target(import);
        return import;
    }

    public void Queued(Guid jobId) => JobId = jobId;

    /// <summary>Файл открыт: вид и версии известны ещё до разбора — их отчёт называет и при отказе.</summary>
    public void Opened(GrandSmetaSource source)
    {
        Kind = source.Kind;
        ProgramVersion = PlanText.Clip(source.ProgramVersion, VersionLength);
        SchemaVersion = PlanText.Clip(source.SchemaEdition, VersionLength);
    }

    /// <summary>Файл не открылся, но вид или версию до отказа узнали: отчёт называет их и у отказа. Редакции
    /// схемы нет — она выбирается по виду и версии, и выбрать её не удалось.</summary>
    public void Recognised(GrandSmetaKind? kind, string? programVersion)
    {
        Kind = kind;
        ProgramVersion = PlanText.Clip(programVersion, VersionLength);
    }

    /// <summary>Разобрана ЛСР: модель и не разобранное — до решения человека.</summary>
    public void ParsedLocalEstimate(string? number, string? name, string parsed, string unparsed, DateTimeOffset now)
    {
        EnsureParsing();
        LsrNumber = PlanText.Clip(number, LsrNumberLength);
        LsrName = PlanText.Clip(name, LsrNameLength);
        Parsed = parsed;
        Unparsed = unparsed;
        Finish(EstimateImportState.Parsed, now);
    }

    /// <summary>Разобран документ, который редакцией не становится (ВОР, КА): только вид.</summary>
    public void ParsedOther(DateTimeOffset now)
    {
        EnsureParsing();
        Finish(EstimateImportState.Parsed, now);
    }

    public void Reject(string code, string text, DateTimeOffset now)
    {
        EnsureParsing();
        RefusalCode = PlanText.Clip(code, RefusalCodeLength);
        RefusalText = text;
        Finish(EstimateImportState.Rejected, now);
    }

    /// <summary>Отказ отменить импорт, из которого уже создана редакция: файл теперь держит она.</summary>
    public void EnsureCancellable()
    {
        if (State == EstimateImportState.Committed)
            throw new ConflictException(
                "Импорт отменить нельзя: из него уже создана редакция сметы. Не нужна редакция — удалите её саму.");
    }

    private void EnsureParsing()
    {
        if (State != EstimateImportState.Parsing)
            throw new ConflictException("Разбор этого импорта уже завершён другой попыткой.");
    }

    private void Finish(EstimateImportState state, DateTimeOffset now)
    {
        State = state;
        ParsedAt = now;
    }
}
