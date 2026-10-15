using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Исправление счёта, требующего исправления (E2, issue #1100, spec v3.3 п. 1.4): причина и ВСЁ
/// содержимое счёта одним запросом — шапка, строки с их частями разноски и части счёта целиком.
///
/// <para><b>Всё содержимое, а не правка по кускам.</b> У обычной правки адресов десять, и у каждого своя
/// запись; у исправления запись одна — версия и книга, — и посчитать её можно только по счёту целиком:
/// «не хуже, чем было» и строки книги сравнивают счёт до и после, а не шапку отдельно от разноски.</para>
///
/// <para>Части разноски присылаются без идентификаторов — клеткой «строка × цель», как у матрицы
/// разноски: у новой строки идентификатора ещё нет, и сослаться на неё частью иначе было бы нечем.</para>
/// </summary>
/// <param name="Reason">Причина — обязательна: она ляжет в версию и в журнал.</param>
/// <param name="Requisites">Шапка — тем же видом, что у правки счёта (<see cref="InvoiceSaveRequest" />).</param>
/// <param name="Lines">Строки — тем же видом, что у замены строк, и у каждой ещё <c>parts</c>: части её
/// разноски (<c>[]</c> — не разнесена).</param>
/// <param name="Document">Части счёта целиком суммой — только у счёта без строк; <c>[]</c> — их нет.</param>
/// <param name="Stamp">Отметка предпросмотра (<see cref="CorrectionPreviewView.Stamp" />). Записи —
/// обязательна, предпросмотру не нужна.</param>
public sealed record InvoiceCorrectionRequest(
    string? Reason, JsonElement Requisites, IReadOnlyList<JsonElement>? Lines, IReadOnlyList<JsonElement>? Document,
    string? Stamp = null);

/// <summary>Исправление, разобранное до связки: значения строк проверены, части ждут вида своей строки.</summary>
internal sealed record CorrectionBody(
    string Reason, JsonElement Requisites, IReadOnlyList<CorrectionLine> Lines, IReadOnlyList<JsonElement> Document,
    string? Stamp);

/// <summary>Строка исправления: идентификатор (нет — новая), значения и части её разноски.</summary>
internal sealed record CorrectionLine(Guid? Id, InvoiceLineValues Values, IReadOnlyList<JsonElement> Parts);

/// <summary>
/// Что запишет исправление — ответ предпросмотра: номер версии, день записи D и строки книги, включая
/// открытие и выравнивание. Отметку запись сверит со своей.
/// </summary>
/// <param name="Version">Номер версии, которую запишет исправление (номер уходящей версии).</param>
/// <param name="Day">День записи D — им исправление ляжет в книгу.</param>
/// <param name="Stamp">Отметка: её присылает запись (<see cref="InvoiceCorrectionRequest.Stamp" />).</param>
public sealed record CorrectionPreviewView(
    int Version, DateOnly Day, IReadOnlyList<CorrectionRecordView> Records, string Stamp);

/// <summary>Запись книги, которую ляжет исправлением.</summary>
/// <param name="Kind"><c>opening</c> — деньги счёта, как они лежали до первой записи; <c>alignment</c> —
/// сдвиг допуском, не записанный раньше; <c>correction</c> — само исправление.</param>
public sealed record CorrectionRecordView(
    string Kind, DateOnly Day, decimal Tolerance, IReadOnlyList<CorrectionLineView> Lines);

/// <summary>Строка книги: ячейка (цель доли; без цели — остаток счёта), день и деньги.</summary>
public sealed record CorrectionLineView(
    Guid? ConstructionId, Guid? SectionId, Guid? ArticleId, DateOnly Day, decimal Gross, decimal Net, bool VatUnknown);

/// <summary>Разбор тела исправления и сборка ответа предпросмотра.</summary>
internal static class InvoiceCorrections
{
    /// <summary>Самая длинная причина: она ложится в журнал ядра, а там длинный текст обрезался бы молча.</summary>
    public const int ReasonLength = 1000;

    /// <param name="preview">Предпросмотр: отметки ещё нет и быть не может.</param>
    public static CorrectionBody Parse(InvoiceCorrectionRequest body, bool preview)
    {
        var reason = body.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            throw new InvalidRequestException(
                "Не названа причина исправления. Она ляжет в версию счёта и в журнал: без неё исправление " +
                "закрытого периода через год не объяснит никто.");
        if (reason.Length > ReasonLength)
            throw new InvalidRequestException(
                $"Причина длиннее {ReasonLength} знаков. Коротко назовите, что и почему исправлено; подробности — в примечании счёта.");

        if (body.Requisites.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException(
                "Шапка счёта («requisites») не прислана. Исправление заменяет содержимое счёта целиком, и " +
                "отсутствие поля прочитать как «шапку не менять» нельзя.");

        if (body.Lines is null)
            throw new InvalidRequestException(
                "Набор строк не прислан. Пустой набор — это «lines»: [], и он означает «строк нет». Исправление " +
                "заменяет содержимое счёта целиком, и «не менять» прочитать из отсутствия нельзя.");

        if (body.Document is null)
            throw new InvalidRequestException(
                "Части счёта целиком («document») не присланы. У счёта со строками это «document»: [] — " +
                "отсутствие поля прочитать как «не менять» нельзя.");

        if (!preview && string.IsNullOrEmpty(body.Stamp))
            throw new InvalidRequestException(
                "Не прислана отметка предпросмотра («stamp»). Исправление записывается только таким, каким его " +
                "показал предпросмотр: сначала посчитайте его — он назовёт день и строки книги.");

        var lines = body.Lines.Select((line, index) => new CorrectionLine(
            InvoiceLineRequests.Id(line, index + 1),
            InvoiceLineRequests.Values(line, index + 1),
            Parts(line, index + 1))).ToList();
        InvoiceLineEndpoints.EnsureIdsDistinct([.. lines.Select(l => (l.Id, l.Values))]);

        return new CorrectionBody(reason, body.Requisites, lines, body.Document, body.Stamp);
    }

    /// <summary>Части строки — обязательны: набор заменяет разноску строки целиком.</summary>
    private static IReadOnlyList<JsonElement> Parts(JsonElement line, int number)
    {
        try
        {
            return AllocationMatrixEndpoints.Parts(line);
        }
        catch (InvalidRequestException e)
        {
            throw new InvalidRequestException($"Строка {number}: {e.Message}", e);
        }
    }

    public static CorrectionPreviewView Preview(CorrectionDone done) => new(
        done.Version, done.Records[^1].Day,
        [.. done.Records.Select(r => new CorrectionRecordView(Kind(r.Kind), r.Day, r.Tolerance,
            [.. r.Lines.Select(l => new CorrectionLineView(l.Cell.Target?.ConstructionId, l.Cell.Target?.SectionId,
                l.Cell.Target?.ArticleId, l.Day, l.Gross, l.Net, l.VatUnknown))]))],
        done.Stamp);

    /// <summary>Вид записи словом ответа. Оплату и её отмену исправление не пишет, но слово есть у каждого вида.</summary>
    private static string Kind(InvoiceBookKind kind) => kind switch
    {
        InvoiceBookKind.Opening => "opening",
        InvoiceBookKind.Alignment => "alignment",
        InvoiceBookKind.Correction => "correction",
        InvoiceBookKind.Payment => "payment",
        _ => "unpayment",
    };
}
