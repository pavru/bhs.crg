namespace BHS.CRG.Domain.Activity;

/// <summary>
/// Каким путём сделано действие (ТЗ CORE-28.1, issue #1336): интерфейс, агент через MCP или сам
/// экземпляр.
///
/// <para><b>Классом, а не перечислением,</b> — потому что этим же значением помечается адрес
/// (<c>MapMcp(...).WithMetadata(ActivityChannel.Mcp)</c>), а метаданные адреса читаются по типу
/// ссылочного класса. Разбирать путь <c>/mcp</c> строкой было бы хрупче: адрес переименуют, и канал
/// молча станет <see cref="Ui" />.</para>
///
/// <para>Хранится строкой <see cref="Code" />: новое значение (будущий «ассистент») добавляется без
/// миграции существующих записей.</para>
/// </summary>
public sealed class ActivityChannel
{
    /// <summary>Ширина колонки кода — одна на запись журнала и задачу.</summary>
    public const int CodeMax = 16;

    /// <summary>Интерфейс и API по токену — всё, что пришло запросом не через MCP.</summary>
    public static readonly ActivityChannel Ui = new("ui", "Интерфейс");

    /// <summary>Внешний агент через MCP — от имени владельца токена.</summary>
    public static readonly ActivityChannel Mcp = new("mcp", "Агент (MCP)");

    /// <summary>Сам экземпляр: старт, расписание, задача без заказчика.</summary>
    public static readonly ActivityChannel System = new("system", "Система");

    /// <summary>Все каналы — перечень для отбора и для отказа на неизвестное значение.</summary>
    public static IReadOnlyList<ActivityChannel> All { get; } = [Ui, Mcp, System];

    private ActivityChannel(string code, string title)
    {
        Code = code;
        Title = title;
    }

    public string Code { get; }

    public string Title { get; }

    /// <summary>Канал по коду; <c>null</c> — такого нет.</summary>
    public static ActivityChannel? Find(string? code) =>
        All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.Ordinal));

    /// <summary>
    /// Канал по ХРАНИМОМУ коду — из записи базы или копии, а не из запроса: известный — он сам, незнакомый
    /// (код более новой версии — например, будущего ассистента) — сохраняется как есть, с кодом вместо
    /// названия. Подменить его известным значило бы записать ложное «через интерфейс», а журнал — только на
    /// дописывание, и после обновления исправить это было бы нечем (ревью PR #1997). В отбор такой канал не
    /// попадает (<see cref="All" />), но в «все каналы» запись видна.
    ///
    /// <c>null</c> — кода нет (запись до каналов: правило <see cref="ForLegacy" />) или он шире колонки, то
    /// есть его не писала ни одна версия: такая копия повреждена, и записать её дословно значило бы отказ базы.
    /// </summary>
    public static ActivityChannel? FromStored(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null
        : Find(code) ?? (code.Length <= CodeMax ? new ActivityChannel(code, code) : null);

    /// <summary>
    /// Канал записи, сделанной до появления каналов (миграция и копия старого формата): без автора —
    /// <see cref="System" />, с автором — <see cref="Ui" />.
    ///
    /// ⚠️ Это не догадка: до канала ни один путь MCP журнал не писал (сверено по коду, design D5), так
    /// что запись с автором сделана интерфейсом. «Неизвестно» утверждало бы незнание там, где ответ
    /// известен. Правило одно на миграцию и восстановление копии — разойтись им нельзя.
    /// </summary>
    public static ActivityChannel ForLegacy(Guid? actorId) => actorId is null ? System : Ui;

    public override string ToString() => Code;
}
