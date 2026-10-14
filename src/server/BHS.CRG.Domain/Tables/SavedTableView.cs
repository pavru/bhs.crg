namespace BHS.CRG.Domain.Tables;

/// <summary>
/// Сохранённое представление таблицы — настройка экрана, которой человек дал имя (ТЗ CORE-33;
/// задача G3a, issue #1095): колонки и их порядок, отбор, сортировка, итоги, закрепление.
///
/// <para><b>Своя таблица, а не ключ настроек пользователя.</b> Общее представление не принадлежит
/// тому, кто его читает; представлений много, у каждого имя, автор и версия. Ключом в
/// <c>user_settings</c> это не выразить: либо ключи перестали бы быть объявленными, либо весь список
/// лёг бы одним значением — без версии строки и без чтения чужими.</para>
///
/// <para><b>Состояние хранится ЦЕЛИКОМ, а не отличием от основы.</b> В адресе экрана лежит отличие —
/// так адрес короче. В базе отличие означало бы, что правка представления модуля молча меняет все
/// сохранённые из него; поэтому <see cref="BasedOn" /> — только «откуда сделано», значений из него
/// никто не берёт.</para>
/// </summary>
public class SavedTableView
{
    public const int TitleMax = 120;
    public const int AddressMax = 128;

    // ReSharper disable once UnusedMember.Local — конструктор для EF.
    private SavedTableView() { }

    public Guid Id { get; private set; }

    /// <summary>Адрес таблицы, как его объявил модуль: <c>costs.invoices</c>.</summary>
    public string TableAddress { get; private set; } = "";

    /// <summary>Код представления модуля, из которого это сделано; null — из таблицы целиком.</summary>
    public string? BasedOn { get; private set; }

    /// <summary>
    /// Автор. null — учётной записи больше нет: у общего представления это «автор удалён», личное
    /// без автора не живёт (его убирает удаление пользователя).
    /// </summary>
    public Guid? OwnerId { get; private set; }

    public string Title { get; private set; } = "";

    /// <summary>Общее — видно всем, кому открыта таблица; иначе — только автору.</summary>
    public bool Shared { get; private set; }

    /// <summary>Состояние экрана — JSON, форму держит <c>SavedTableViewRules</c>.</summary>
    public string State { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Версия строки (xmin): по ней правка узнаёт, что представление тем временем изменили.</summary>
    public uint RowVersion { get; private set; }

    public static SavedTableView Create(
        string tableAddress, string? basedOn, Guid? ownerId, string title, bool shared, string state) => new()
    {
        Id = Guid.NewGuid(),
        TableAddress = tableAddress,
        BasedOn = basedOn,
        OwnerId = ownerId,
        Title = title,
        Shared = shared,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Представление из резервной копии — со своим идентификатором: закладки на общее представление
    /// несут его в адресе и после переноса обязаны открываться.
    /// </summary>
    public static SavedTableView Restore(
        Guid id, string tableAddress, string? basedOn, string title, string state,
        DateTimeOffset createdAt, DateTimeOffset updatedAt) => new()
    {
        Id = id,
        TableAddress = tableAddress,
        BasedOn = basedOn,
        Title = title,
        Shared = true,
        State = state,
        CreatedAt = createdAt,
        UpdatedAt = updatedAt,
    };

    /// <summary>
    /// То же представление из копии поверх лежащего здесь: всё, что копия несёт, — вместе с адресом
    /// таблицы и временем правки. Время берётся из копии, а не «сейчас»: восстановление не правка.
    /// </summary>
    public void Carry(string tableAddress, string? basedOn, string title, string state, DateTimeOffset updatedAt)
    {
        TableAddress = tableAddress;
        BasedOn = basedOn;
        Title = title;
        State = state;
        UpdatedAt = updatedAt;
    }

    public void Change(string title, bool shared, string state)
    {
        Title = title;
        Shared = shared;
        State = state;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
