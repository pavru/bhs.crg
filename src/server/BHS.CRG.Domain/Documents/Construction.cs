using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class Construction : Entity
{
    public string Name { get; private set; } = default!;
    public Guid CreatedByUserId { get; private set; }

    /// <summary>Объект-профиль уровня (issue #258) — DomainObject профиль-типа на scope стройки, если создан.
    /// Простой nullable-указатель (не DB-FK: объект — часть агрегата контейнера, каскад по scope).</summary>
    public Guid? ProfileObjectId { get; private set; }
    public void SetProfileObject(Guid objectId) { ProfileObjectId = objectId; TouchUpdatedAt(); }

    /// <summary>
    /// Часовой пояс стройки в форме IANA (<c>Europe/Moscow</c>) — ТЗ CORE-5. По нему код считает
    /// СУТКИ: смена через полночь относится к той дате, которой она началась на стройке (WORK-8).
    ///
    /// <para>⚠️ <c>null</c> здесь — не «неизвестно», а «как у компании»: пояс берётся из настройки
    /// ядра. Это решение, а не пропуск. Записать умолчание в каждую стройку означало бы, что смена
    /// пояса компании не меняет НИ ОДНУ из них — включая те, где пояс никто и не выбирал, — а
    /// разница обнаружилась бы датами в документах.</para>
    ///
    /// <para>Колонка, а не поле профиля стройки: профиль настраивает заказчик, а по этому значению
    /// считает КОД. Поле, которое можно переименовать или удалить из схемы, для этого не годится.</para>
    /// </summary>
    public string? TimeZoneId { get; private set; }

    /// <summary>
    /// Система-источник внешнего идентификатора (<c>1С</c>) — ТЗ CORE-5, для будущей загрузки
    /// строек извне. Пара с <see cref="ExternalCode" />: код без системы не адресует ничего, потому
    /// что коды разных систем совпадают свободно.
    /// </summary>
    public string? ExternalSystem { get; private set; }

    /// <summary>Код стройки в системе-источнике. Смотри <see cref="ExternalSystem" />.</summary>
    public string? ExternalCode { get; private set; }

    private readonly List<Section> _sections = [];
    public IReadOnlyList<Section> Sections => _sections.AsReadOnly();

    private Construction() { }

    public static Construction Create(string name, Guid userId)
        => new() { Name = name, CreatedByUserId = userId };

    /// <summary>
    /// Часовой пояс стройки. <c>null</c> — «как у компании»: стройка возвращается к настройке ядра,
    /// а не остаётся с последним выбранным поясом.
    /// </summary>
    public void SetTimeZone(string? ianaId)
    {
        TimeZoneId = string.IsNullOrWhiteSpace(ianaId) ? null : ianaId.Trim();
        TouchUpdatedAt();
    }

    /// <summary>
    /// Внешний идентификатор — ТОЛЬКО парой. Половина пары не адресует ничего и при следующей
    /// загрузке извне выглядела бы совпадением: коды разных систем пересекаются.
    /// </summary>
    public void SetExternalId(string? system, string? code)
    {
        var s = string.IsNullOrWhiteSpace(system) ? null : system.Trim();
        var c = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        if ((s is null) != (c is null))
            throw new InvalidRequestException(
                "Внешний идентификатор задаётся парой «система + код»: одна половина не адресует ничего.");
        ExternalSystem = s;
        ExternalCode = c;
        TouchUpdatedAt();
    }

    /// <summary>Восстановление из резервной копии (issue #833): идентификатор и время — как были.</summary>
    public static Construction Restore(Guid id, string name, Guid createdByUserId, Guid? profileObjectId,
        DateTimeOffset createdAt, DateTimeOffset updatedAt,
        string? timeZoneId = null, string? externalSystem = null, string? externalCode = null)
        => new()
        {
            Id = id, Name = name, CreatedByUserId = createdByUserId, ProfileObjectId = profileObjectId,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
            TimeZoneId = timeZoneId, ExternalSystem = externalSystem, ExternalCode = externalCode,
        };

    public void Rename(string name) { Name = name; TouchUpdatedAt(); }
}
