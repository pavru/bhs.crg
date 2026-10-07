namespace BHS.CRG.Modules.Ports;

/// <summary>
/// Что прочитано в документе по профилю модуля.
///
/// <para>⚠️ Перечни запрошенного приезжают вместе с ответом, и это не избыточность. Профиль правит
/// администратор: уберёт он поле — ключа в <see cref="Fields" /> не будет вовсе. Получателю надо
/// отличать «не спрашивали» от «спросили и не нашли»: первое — не повод писать «в скане номера
/// нет».</para>
/// </summary>
/// <param name="AskedFields">Ключи скалярных полей, которые были в запросе.</param>
/// <param name="Fields">Значения по ключам. Ключ есть для каждого запрошенного поля; <c>null</c> —
/// спросили, в документе не нашлось.</param>
/// <param name="AskedColumns">Ключи колонок таблицы; пусто — таблицы у вида профиля нет.</param>
/// <param name="Rows">Строки таблицы в порядке документа.</param>
/// <param name="RowsProblem">
/// Таблицу спросили, а ответ по ней не разобрать: модель её не вернула или вернула не таблицей.
/// <c>null</c> — таблица разобрана, и пустой <see cref="Rows" /> тогда значит «строк в документе
/// нет». Без этого поля «не разобрали» и «строк нет» выглядели бы одинаково — нулём строк.
/// </param>
/// <param name="Engine">Кто распознал — движок и модель, для журнала и для вопроса «почему плохо».</param>
public sealed record ModuleRecognitionResult(
    IReadOnlyList<string> AskedFields,
    IReadOnlyDictionary<string, string?> Fields,
    IReadOnlyList<string> AskedColumns,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
    string? RowsProblem,
    string? Engine);

/// <summary>
/// Распознавание документа для модуля (ТЗ CORE-Q6, задача B1b, issue #1077).
///
/// <para>Распознавание — служба ядра: движки, их выбор, запрос к модели и разбор ответа живут там.
/// Модуль называет СВОЙ заводской профиль кодом (<see cref="IAppModule.RecognitionProfiles" />) и
/// получает значения по его ключам. Читается профиль с правками администратора — так же, как его
/// читают наборы данных: заводское содержимое — умолчание, а не закон.</para>
///
/// <para>⚠️ Отказ — исключением <c>RecognitionRefusedException</c> (домен), с видом причины: не
/// настроено, недоступно, ответа не было. Ответ без единого значения — тоже отказ: вернись он
/// результатом, получатель разложил бы пустоту по полям записи, и это выглядело бы распознанным
/// документом.</para>
///
/// <para>⚠️ Вызов долгий — десятки секунд на файл. Звать из фоновой задачи
/// (<see cref="IModuleJobs" />), а не из запроса; до постановки — <see cref="EnsureReadyAsync" />,
/// чтобы «не настроено» пришло ответом на нажатие, а не строкой в журнале задач.</para>
/// </summary>
public interface IModuleRecognition
{
    /// <summary>
    /// Есть ли кому распознавать. Молчит — можно ставить задачу; иначе отказ с причиной.
    ///
    /// Проверка не обещает успеха: движок может отказать и после неё. Она отсекает только то, что
    /// известно заранее и повтором не лечится.
    /// </summary>
    Task EnsureReadyAsync(string profileCode, CancellationToken ct = default);

    /// <param name="profileCode">Код заводского профиля — как модуль назвал его в объявлении.</param>
    /// <param name="content">Файл целиком: документ читается одним вызовом.</param>
    /// <param name="mimeType">Тип содержимого — PDF или изображение.</param>
    Task<ModuleRecognitionResult> RecognizeAsync(
        string profileCode, byte[] content, string mimeType, CancellationToken ct = default);
}
