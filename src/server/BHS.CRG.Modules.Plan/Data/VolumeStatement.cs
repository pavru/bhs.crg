using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Ведомость объёмов работ — приложение к смете (change <c>plan-11-conjuncture</c>, решение В-7 (а), ТЗ PLAN-7):
/// файл и разобранные строки для просмотра. План сметы, «Ход работ» и график по ней не считаются — объёмы плана
/// приходят из редакции ЛСР. Денег в ВОР нет, правило D4 зонтика её не касается.
/// </summary>
public sealed class VolumeStatement : EstimateAttachment
{
    private VolumeStatement() { }

    public List<VolumeStatementLine> Lines { get; private set; } = [];

    public static VolumeStatement From(EstimateImport import, ParsedVolumeStatement model, DateTimeOffset now, Guid? by, string? byName)
    {
        var statement = new VolumeStatement();
        statement.Fill(import, model.Requisites.Number, model.Requisites.Name, model.Requisites.CreationDate,
            model.Works.Count(), model.PositionsInFile, now, by, byName);
        var order = 0;
        foreach (var chapter in model.Chapters)
            foreach (var work in chapter.Works)
                statement.Lines.Add(VolumeStatementLine.From(statement.Id, ++order, chapter, work));
        return statement;
    }
}

/// <summary>
/// Строка ведомости: наименование, единица, объём и обоснование — шифр (<see cref="Code" />), ссылка на чертежи
/// (<see cref="Drawings" />) и примечание сметчика. Формула — текстом, как её записал сметчик.
/// </summary>
public sealed class VolumeStatementLine
{
    public const int NumberLength = 100;
    public const int CodeLength = 300;
    public const int UnitLength = 100;

    private VolumeStatementLine() { }

    public Guid Id { get; private set; }

    public Guid StatementId { get; private set; }

    public int Order { get; private set; }

    public int ChapterIndex { get; private set; }

    public string ChapterCaption { get; private set; } = string.Empty;

    public string Number { get; private set; } = string.Empty;

    public string Caption { get; private set; } = string.Empty;

    public string? Code { get; private set; }

    public string? Units { get; private set; }

    public decimal Quantity { get; private set; }

    public string? Formula { get; private set; }

    public string? Drawings { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Подчинённая работа (входит в объём вышестоящей).</summary>
    public bool IsSubordinate { get; private set; }

    internal static VolumeStatementLine From(Guid statementId, int order, VolumeChapter chapter, VolumeWork work) => new()
    {
        Id = Guid.NewGuid(),
        StatementId = statementId,
        Order = order,
        ChapterIndex = chapter.Index,
        ChapterCaption = chapter.Caption,
        Number = PlanText.Clip(work.Number, NumberLength) ?? string.Empty,
        Caption = work.Caption,
        Code = PlanText.Clip(work.Code, CodeLength),
        Units = PlanText.Clip(work.Units, UnitLength),
        Quantity = work.Quantity,
        Formula = work.Formula,
        Drawings = work.Comment1,
        Note = work.Comment2,
        IsSubordinate = work.IsSubordinate,
    };
}
