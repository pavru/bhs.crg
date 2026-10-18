using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Storage;

/// <summary>Бумага профиля печати. ⚠️ Имена — формат хранения: они лежат в базе и в копии.</summary>
public enum PaperFormat
{
    A4,
    A3,
    A5,
}

/// <summary>Ориентация листа. ⚠️ Имена — формат хранения.</summary>
public enum PageOrientation
{
    Portrait,
    Landscape,
}

/// <summary>
/// Вписывание. По высоте не вписывается никогда — «сколько выйдет»: счёт на три страницы, сжатый в
/// одну, не прочесть ни человеку, ни модели. ⚠️ Имена — формат хранения.
/// </summary>
public enum PageFit
{
    /// <summary>Вписать в ширину страницы.</summary>
    Width,

    /// <summary>Не вписывать: бумага, ориентация и поля профиля, масштаб — как вышел.</summary>
    None,
}

/// <summary>Поля страницы в миллиметрах.</summary>
public sealed record PageMarginsMm(int Top, int Bottom, int Left, int Right);

/// <summary>
/// Отказ правки профиля печати, называющий поля формы (issue #1454): экран подсвечивает поле, а не
/// показывает баннер. Пути — как в теле запроса: <c>name</c>, <c>paper</c>, <c>margins.top</c>.
/// </summary>
public sealed class PrintProfileFieldException(string message, params RefusalDetail[] details)
    : InvalidRequestException(message), IDetailedRefusal
{
    public IReadOnlyList<RefusalDetail> Details { get; } = details;

    public static PrintProfileFieldException Of(string path, string code, string message) =>
        new(message, new RefusalDetail(code, path, message));
}

/// <summary>Имя профиля печати занято — отказ 409 с полем <c>name</c>.</summary>
public sealed class PrintProfileNameTakenException(string name, Exception? inner = null)
    : ConflictException($"Профиль печати «{name}» уже есть — выберите другое название.", inner), IDetailedRefusal
{
    public IReadOnlyList<RefusalDetail> Details => [new("taken", "name", Message)];
}

/// <summary>
/// Профиль печати читаемого образа книги Excel (issue #1454, эпик #1452): как разложить на бумаге
/// лист, автор которого раскладку не задал. На экране — «Профили печати»; в коде имя другое, потому
/// что «печать» в системе уже занята выпуском документов и «печатью и подписью» в счетах.
///
/// <para><b>Системный профиль один</b> («по умолчанию») — с идентификатором-константой кода
/// (<see cref="SystemId" />). Константа, а не случайный: восстановленная копия другой установки
/// несёт системный профиль под тем же идентификатором, и второго не появляется. Его правят, но не
/// удаляют и не переименовывают. Сброс к заводским копирует константы кода — и только по кнопке:
/// тихого пересева при старте нет, иначе выпуск менял бы раскладку без решения администратора.</para>
///
/// <para><b>Отпечаток</b> (<see cref="Stamp" />) — параметры без имени. Запись образа помнит его на
/// момент построения: разошёлся с нынешним — профиль с тех пор правили. Переименование образ не
/// «старит»: раскладка от имени не зависит.</para>
/// </summary>
public class RenditionLayoutProfile : Entity
{
    public static readonly Guid SystemId = new("6f1d3c2a-0b7e-4c59-9a43-5e2a7d4b1454");

    public const string SystemName = "по умолчанию";
    public const int NameMax = 80;
    public const int MarginMax = 40;

    /// <summary>
    /// Меньше полезной ширины или высоты профиль не оставляет: при сотне миллиметров на А5 с полями по
    /// 40 таблицу уже не разместить, а сохранённый такой профиль давал бы вид, который не прочесть.
    /// </summary>
    public const int UsefulMinMm = 100;

    public static readonly PageMarginsMm FactoryMargins = new(5, 5, 5, 5);

    public string Name { get; private set; } = default!;
    public PaperFormat Paper { get; private set; }
    public PageOrientation Orientation { get; private set; }
    public PageFit Fit { get; private set; }
    public int MarginTop { get; private set; }
    public int MarginBottom { get; private set; }
    public int MarginLeft { get; private set; }
    public int MarginRight { get; private set; }
    public bool IsSystem { get; private set; }

    public PageMarginsMm Margins => new(MarginTop, MarginBottom, MarginLeft, MarginRight);

    private RenditionLayoutProfile() { }

    /// <summary>Системный профиль с заводскими параметрами — для сидера и для сброса.</summary>
    public static RenditionLayoutProfile System()
    {
        var profile = new RenditionLayoutProfile { Id = SystemId, Name = SystemName, IsSystem = true };
        profile.Set(PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, FactoryMargins);
        return profile;
    }

    public static RenditionLayoutProfile Create(
        string name, PaperFormat paper, PageOrientation orientation, PageFit fit, PageMarginsMm margins)
    {
        var profile = new RenditionLayoutProfile { Name = CheckedName(name) };
        Check(paper, orientation, fit, margins);
        profile.Set(paper, orientation, fit, margins);
        return profile;
    }

    /// <summary>Профиль из резервной копии — под своим идентификатором и с её временем.</summary>
    public static RenditionLayoutProfile Restore(
        Guid id, string name, PaperFormat paper, PageOrientation orientation, PageFit fit, PageMarginsMm margins,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        var profile = Create(name, paper, orientation, fit, margins);
        profile.Id = id;
        profile.CreatedAt = createdAt;
        profile.UpdatedAt = updatedAt;
        return profile;
    }

    /// <summary>
    /// Правка. У системного имя не меняется: на экране он «по умолчанию», и это имя — договор с
    /// человеком, а не подпись, которую можно сменить.
    /// </summary>
    public void Update(string name, PaperFormat paper, PageOrientation orientation, PageFit fit, PageMarginsMm margins)
    {
        var checkedName = CheckedName(name);
        if (IsSystem && checkedName != SystemName)
            throw PrintProfileFieldException.Of("name", "system_name",
                $"Системный профиль называется «{SystemName}» — переименовать его нельзя.");
        Check(paper, orientation, fit, margins);
        Name = checkedName;
        Set(paper, orientation, fit, margins);
        TouchUpdatedAt();
    }

    /// <summary>Вернуть заводские параметры. Только системному: у своего профиля заводских нет.</summary>
    public void ResetToFactory()
    {
        if (!IsSystem)
            throw new ConflictException("Сбросить к заводским можно только системный профиль: у своего профиля заводских параметров нет.");
        Set(PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, FactoryMargins);
        TouchUpdatedAt();
    }

    /// <summary>Системный профиль не удаляется: без него первому построению образа не по чему раскладывать.</summary>
    public void EnsureDeletable()
    {
        if (IsSystem)
            throw new ConflictException(
                $"Профиль «{SystemName}» — системный: по нему строятся все виды, пока не выбран другой. " +
                "Удалить его нельзя — только изменить или сбросить к заводским.");
    }

    /// <summary>Параметры совпадают с заводскими — сбрасывать нечего.</summary>
    public bool IsFactory => Paper == PaperFormat.A4 && Orientation == PageOrientation.Portrait && Fit == PageFit.Width
                             && Margins == FactoryMargins;

    /// <summary>
    /// Отпечаток параметров без имени. Приставка — версия формата: новый параметр профиля меняет
    /// отпечаток всех профилей, и все виды честно станут «построены по прежним параметрам».
    /// </summary>
    public string Stamp => $"v1:{Paper}:{Orientation}:{Fit}:{MarginTop}:{MarginBottom}:{MarginLeft}:{MarginRight}";

    /// <summary>Лист бумаги в миллиметрах с учётом ориентации: ширина и высота.</summary>
    public static (int Width, int Height) SheetMm(PaperFormat paper, PageOrientation orientation)
    {
        var (shorter, longer) = paper switch
        {
            PaperFormat.A3 => (297, 420),
            PaperFormat.A5 => (148, 210),
            _ => (210, 297),
        };
        return orientation == PageOrientation.Landscape ? (longer, shorter) : (shorter, longer);
    }

    private void Set(PaperFormat paper, PageOrientation orientation, PageFit fit, PageMarginsMm margins)
    {
        Paper = paper;
        Orientation = orientation;
        Fit = fit;
        (MarginTop, MarginBottom, MarginLeft, MarginRight) = (margins.Top, margins.Bottom, margins.Left, margins.Right);
    }

    private static string CheckedName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) throw PrintProfileFieldException.Of("name", "required", "Назовите профиль печати.");
        if (trimmed.Length > NameMax)
            throw PrintProfileFieldException.Of("name", "too_long", $"Название профиля печати — не длиннее {NameMax} знаков.");
        return trimmed;
    }

    private static void Check(PaperFormat paper, PageOrientation orientation, PageFit fit, PageMarginsMm margins)
    {
        if (!Enum.IsDefined(paper)) throw PrintProfileFieldException.Of("paper", "unknown", "Бумага — A4, A3 или A5.");
        if (!Enum.IsDefined(orientation))
            throw PrintProfileFieldException.Of("orientation", "unknown", "Ориентация — книжная или альбомная.");
        if (!Enum.IsDefined(fit)) throw PrintProfileFieldException.Of("fit", "unknown", "Вписывание — по ширине или не вписывать.");

        var outOfRange = new[] { ("margins.top", margins.Top), ("margins.bottom", margins.Bottom),
                ("margins.left", margins.Left), ("margins.right", margins.Right) }
            .Where(side => side.Item2 is < 0 or > MarginMax).ToList();
        if (outOfRange.Count > 0)
        {
            var text = $"Поля — от 0 до {MarginMax} мм.";
            throw new PrintProfileFieldException(text, [.. outOfRange.Select(side => new RefusalDetail("out_of_range", side.Item1, text))]);
        }

        // Остаток называется числом: экран показывает «остаётся N мм» под схемой листа, и человеку
        // видно, на сколько уменьшить поля.
        var (width, height) = SheetMm(paper, orientation);
        var details = new List<RefusalDetail>();
        var texts = new List<string>();
        var usefulWidth = width - margins.Left - margins.Right;
        var usefulHeight = height - margins.Top - margins.Bottom;
        if (usefulWidth < UsefulMinMm)
        {
            var text = $"По ширине остаётся {usefulWidth} мм — нужно не меньше {UsefulMinMm}.";
            texts.Add(text);
            details.Add(new("too_small", "margins.left", text));
            details.Add(new("too_small", "margins.right", text));
        }
        if (usefulHeight < UsefulMinMm)
        {
            var text = $"По высоте остаётся {usefulHeight} мм — нужно не меньше {UsefulMinMm}.";
            texts.Add(text);
            details.Add(new("too_small", "margins.top", text));
            details.Add(new("too_small", "margins.bottom", text));
        }
        if (details.Count > 0)
            throw new PrintProfileFieldException(
                string.Join(" ", texts) + " Уменьшите поля или возьмите бумагу больше.",
                [.. details]);
    }
}
