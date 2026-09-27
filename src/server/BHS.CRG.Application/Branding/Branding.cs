namespace BHS.CRG.Application.Branding;

/// <summary>
/// Название продукта и логотип компании — настройки ЭКЗЕМПЛЯРА (ТЗ CORE-25.1, issue #967).
///
/// <para>До настройки — нейтральное общее название и без логотипа. Нейтральное значит «не про
/// заказчика и не про модуль»: экземпляр, на котором включён только учёт работ, не должен
/// называться по исполнительной документации (решение по OVW-Q1 от 17.09.2026). Имя продукта этому
/// условию отвечает и придумывать второе, «совсем уж общее», незачем — оно всё равно оказалось бы
/// именем, только хуже узнаваемым.</para>
/// </summary>
public static class BrandingDefaults
{
    /// <summary>
    /// Название до настройки. Одно на сервер и клиент: клиент берёт его из ответа, а не повторяет у
    /// себя — иначе два умолчания разошлись бы, и страница входа спорила бы с шапкой.
    /// </summary>
    public const string ProductName = "BHS.CRG";

    /// <summary>
    /// Имя системного ассета, под которым лежит логотип. Оно же — путь в шаблоне Typst:
    /// <c>image("/assets/company-logo.png")</c> (расширение — от загруженного файла).
    ///
    /// <para>Логотип — обычный ассет уровня «система» (ТЗ CORE-25.1: «доступен шаблонам Typst как
    /// ассет уровня системы»), а не вторая копия рядом. Своё хранилище означало бы два файла, из
    /// которых печатная форма рано или поздно взяла бы устаревший.</para>
    ///
    /// <para>⚠️ Имя с дефисом, а не просто <c>logo</c>: короткое имя уже могли занять на экземплярах
    /// заказчиков своим ассетом, и настройка логотипа молча заменила бы им чужую картинку в
    /// шаблонах.</para>
    /// </summary>
    public const string LogoAssetName = "company-logo";

    /// <summary>Предел длины названия: оно стоит в шапке, на входе и в заголовке вкладки.</summary>
    public const int MaxProductNameLength = 64;
}

/// <param name="ProductName">Действующее название — заданное администратором либо умолчание.</param>
/// <param name="IsCustom">Название задано, а не подставлено умолчанием.</param>
/// <param name="HasLogo">Логотип загружен.</param>
/// <param name="LogoVersion">
/// Метка версии логотипа (момент последней замены). Нужна адресу картинки как <c>?v=</c>: файл
/// отдаётся с длинным кешем, и без метки заменённый логотип остался бы старым у всех, кто уже
/// заходил, — то есть ровно у тех, ради кого замену и делали (та же причина, что у метки знака в
/// <c>index.html</c>, issue #728).
/// </param>
public record BrandingInfo(string ProductName, bool IsCustom, bool HasLogo, string? LogoVersion);

/// <param name="ETag">Сильный тег версии файла — по нему браузер переспрашивает одним 304.</param>
public record BrandingLogo(byte[] Content, string MimeType, string FileName, string ETag);

/// <summary>
/// Чтение и правка фирменного оформления экземпляра (ТЗ CORE-25.1).
///
/// ⚠️ Чтение — АНОНИМНОЕ: название и логотип стоят на странице входа, то есть должны быть видны до
/// входа. Это осознанная выдача наружу: кто дошёл до страницы входа, тот видит, чей это экземпляр.
/// Правка — под правом обслуживания экземпляра (<c>core.system.manage</c>).
/// </summary>
public interface IBrandingService
{
    Task<BrandingInfo> GetAsync(CancellationToken ct = default);

    /// <summary>Пусто или null — снять название, вернуться к умолчанию.</summary>
    Task SetProductNameAsync(string? name, CancellationToken ct = default);

    /// <summary>Логотип с содержимым; null — логотипа нет.</summary>
    Task<BrandingLogo?> GetLogoAsync(CancellationToken ct = default);

    /// <summary>Загрузить или заменить логотип. Возвращает новое состояние оформления.</summary>
    Task<BrandingInfo> SetLogoAsync(
        byte[] content, string fileName, string mimeType, CancellationToken ct = default);

    /// <summary>Убрать логотип. Файл в хранилище удаляется вместе со строкой ассета.</summary>
    Task<BrandingInfo> RemoveLogoAsync(CancellationToken ct = default);
}
