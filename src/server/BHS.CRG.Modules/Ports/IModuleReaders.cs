using BHS.CRG.Modules.Access;

namespace BHS.CRG.Modules.Ports;

/// <summary>
/// Откуда модуль берёт параметр доступа контракта (<see cref="ModuleReader" />, ТЗ CORE-34, AUTH-6).
///
/// <para>Три источника, и каждый назван методом, а не угадан по обстановке: по месту вызова должно
/// быть видно, в чьих правах пойдёт чтение. Угадывание («есть запрос — его пользователь, нет — система»)
/// в фоновой операции пользователя молча давало бы отказ системе вместо его прав.</para>
///
/// <para>⚠️ Права считаются ИЗ БАЗЫ, тем же счётчиком, что у ворот адресов: ключ модуля попадает в
/// параметр по правилу <see cref="ModuleAccess.IsOpen" />, и право контракта модуль не открывает
/// (AUTH-8.1).</para>
/// </summary>
public interface IModuleReaders
{
    /// <summary>
    /// Пользователь текущего запроса. Вне запроса — исключение: это дефект вызывающего, а не отказ
    /// пользователю (фоновая операция берёт <see cref="ForJobAsync" />).
    /// </summary>
    Task<ModuleReader> ForRequestAsync(CancellationToken ct = default);

    /// <summary>
    /// Фоновая операция — от имени поставившего её (<see cref="ModuleJobRun.UserId" />). Учётной записи
    /// больше нет — параметр системы с причиной: брать чьи-то другие права неоткуда.
    /// </summary>
    Task<ModuleReader> ForJobAsync(ModuleJobRun run, CancellationToken ct = default);

    /// <summary>Задание без человека: контракт ответит ему отказом с этой причиной.</summary>
    ModuleReader ForSystem(string reason);
}
