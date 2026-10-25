using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Почему редакция зафиксирована: какая производная из неё сделана (ТЗ PLAN-5).
/// </summary>
/// <param name="Code">Вид производной: <c>offer</c> — коммерческое предложение (<c>plan-13</c>),
/// <c>schedule</c> — график (<c>plan-19</c>).</param>
/// <param name="Label">Как производную назвать человеку, в именительном: «КП № КП-2026-014», «график от
/// 12.11.2026». Запоминается словами, а не ключом: отказ читают в тосте, и производную в нём надо узнать,
/// не открывая её.</param>
public sealed record EstimateFreeze(string Code, string Label);

/// <summary>
/// Редакция сметы — один импорт файла сметы целиком (change <c>plan-06-estimate-model</c>, ТЗ PLAN-1,
/// PLAN-5, D3 зонтика <c>plan-00-epic</c>).
///
/// <para><b>Редакции сосуществуют, а не сменяют друг друга.</b> Это не версии записи (<c>CORE-36</c>):
/// КП и график делаются из конкретной редакции и читают её и после того, как появилась следующая.
/// Повторный импорт создаёт новую редакцию, а не правит прежнюю.</para>
///
/// <para><b>Фиксация</b> (ТЗ PLAN-5): из редакции сделали производную — и она больше не правится и не
/// удаляется (<see cref="EnsureEditable" />), иначе КП и график разошлись бы со сметой, из которой
/// сделаны, и никто бы об этом не узнал. Фиксируют её <c>plan-13</c> (КП) и <c>plan-19</c> (график)
/// методом <see cref="Freeze" />.</para>
/// </summary>
public sealed class EstimateRevision
{
    public const int SchemaVersionLength = 50;
    public const int ImportKeyLength = 200;
    public const int FileNameLength = 300;
    public const int FreezeCodeLength = 32;
    public const int FreezeLabelLength = 300;
    public const int NormativeBaseLength = 500;

    private EstimateRevision() { }

    public Guid Id { get; private set; }

    public Guid EstimateId { get; private set; }

    /// <summary>Номер в смете: растёт с каждой редакцией и не переиспользуется, пока редакция жива.</summary>
    public int Number { get; private set; }

    /// <summary>Файл источника в хранилище — ради повторного разбора и спора «что было в файле».</summary>
    public string? SourceBlobPath { get; private set; }

    public string? SourceFileName { get; private set; }

    /// <summary>Версия схемы файла, по которой он разобран (ГРАНД-Смета OpenXML, <c>plan-05</c>).</summary>
    public string? SchemaVersion { get; private set; }

    /// <summary>
    /// Нормативная база файла, как её назвала программа (<c>RegionInfo/@RegionName</c>): часть ключа
    /// запомненного сопоставления шифра (<c>plan-08</c>) — один шифр в разных базах бывает в разных единицах.
    /// </summary>
    public string? NormativeBase { get; private set; }

    /// <summary>Ключ импорта — по нему повтор того же файла узнаётся, а не заводит вторую редакцию
    /// (<c>plan-07</c>).</summary>
    public string? ImportKey { get; private set; }

    public DateTimeOffset ImportedAt { get; private set; }

    /// <summary>Кто импортировал — справка, а не связь (учётную запись это удалению не мешает).</summary>
    public Guid? ImportedBy { get; private set; }

    /// <summary>Имя импортировавшего на момент импорта: удалённая учётная запись не делает редакцию
    /// безымянной.</summary>
    public string? ImportedByName { get; private set; }

    public DateTimeOffset? FrozenAt { get; private set; }

    public string? FrozenCode { get; private set; }

    public string? FrozenLabel { get; private set; }

    /// <summary>
    /// Когда строки редакции сопоставляли в последний раз (<c>plan-08</c>). Нужна не ради даты: сопоставление
    /// правит строки, а версию называет редакция (<c>If-Match</c>), — и без записи в саму редакцию две вкладки,
    /// сопоставляющие одну смету, не узнали бы друг о друге.
    /// </summary>
    public DateTimeOffset? MatchedAt { get; private set; }

    public bool Frozen => FrozenAt is not null;

    internal static EstimateRevision Create(Guid estimateId, int number, EstimateRevisionSource source,
        Guid? importedBy, string? importedByName, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            EstimateId = estimateId,
            Number = number,
            SourceBlobPath = source.BlobPath,
            SourceFileName = PlanText.Clip(source.FileName, FileNameLength),
            SchemaVersion = PlanText.Clip(source.SchemaVersion, SchemaVersionLength),
            NormativeBase = PlanText.Clip(source.NormativeBase, NormativeBaseLength),
            ImportKey = PlanText.Clip(source.ImportKey, ImportKeyLength),
            ImportedAt = now,
            ImportedBy = importedBy,
            ImportedByName = importedByName,
        };

    /// <summary>
    /// Зафиксировать редакцию: из неё сделана производная (ТЗ PLAN-5). Повторная фиксация ничего не
    /// меняет — запертая редакция остаётся запертой, и отказ продолжает называть первую производную:
    /// её достаточно, чтобы человек понял, почему правка не прошла.
    /// </summary>
    public void Freeze(EstimateFreeze reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason.Code) || reason.Code.Length > FreezeCodeLength)
            throw new ArgumentException("Вид производной не назван или длиннее допустимого.", nameof(reason));
        if (string.IsNullOrWhiteSpace(reason.Label))
            throw new ArgumentException("Производная не названа: отказ правке не смог бы сказать, что из редакции сделано.", nameof(reason));
        if (Frozen) return;

        FrozenAt = now;
        FrozenCode = reason.Code;
        FrozenLabel = PlanText.Clip(reason.Label, FreezeLabelLength);
    }

    /// <summary>Строки сопоставлены: версия редакции сдвигается (<see cref="MatchedAt" />).</summary>
    public void RowsMatched(DateTimeOffset now) => MatchedAt = now;

    /// <summary>
    /// Отказ правке зафиксированной редакции — 409 с названием производной (ТЗ PLAN-5). Зовут удаление
    /// редакции здесь и сопоставление строк (<c>plan-08</c>, правило решения В-6).
    /// </summary>
    /// <param name="action">Что не выполнено, с заглавной: «Удалить редакцию».</param>
    public void EnsureEditable(string action)
    {
        if (!Frozen) return;
        throw new ConflictException(
            $"{action} нельзя: редакция {Number} зафиксирована — из неё сделано {FrozenLabel}. Производная " +
            "читает редакцию такой, какой её сделали; для изменений импортируйте смету заново — появится " +
            "новая редакция.");
    }
}

/// <summary>Откуда редакция: файл, версия его схемы, ключ импорта (заполняет импорт, <c>plan-07</c>) и
/// нормативная база файла (<c>plan-08</c>).</summary>
public sealed record EstimateRevisionSource(
    string? BlobPath = null, string? FileName = null, string? SchemaVersion = null, string? ImportKey = null,
    string? NormativeBase = null);
