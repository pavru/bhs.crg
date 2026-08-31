using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Application.Jobs;

/// <summary>
/// Чем кончился запуск распознавания.
/// </summary>
/// <param name="JobId">Задача поставлена в очередь; итог спрашивать по нему (<see cref="IJobService.GetAsync" />).</param>
/// <param name="Source">Операция уложилась в вызов и уже выполнена — источник в его новом виде.
/// Так ведут себя короткие профили («Счёт», секунды); ГОСТ-альбом уходит в фон.</param>
/// <param name="Blocked">Запуска не было: распознавать некому (движок не настроен либо уличён в
/// слепоте). Причина — не догадка вызывающего, а ответ <see cref="IRecognitionPreflight" />.</param>
public record RecognitionLaunch(Guid? JobId, DataSetSourceDto? Source, RecognitionBlock? Blocked);

/// <summary>
/// Запуск долгих операций ВМЕСТЕ с их защитами (issue #898).
///
/// Заведено потому, что защит у этих операций больше, чем кажется, и до сих пор все они жили в
/// обработчиках HTTP-эндпоинтов. Пока адаптер был один, это ничего не стоило; со вторым (MCP)
/// означало бы, что агент получает вход в обход того, чем прикрыт человек, — причём молча, потому
/// что «работает» и тот и другой путь.
///
/// Защиты, о которых речь, и почему каждая тут:
///
/// <list type="bullet">
/// <item>«по этой цели уже идёт» — иначе повтор ставит вторую такую же задачу. У экрана кнопка
/// заблокирована, но блокировка живёт во вкладке и не переживает перезагрузку; у агента нет и
/// её, а звать в цикле для него естественно;</item>
/// <item>предполёт движка распознавания — и обязательно ПОСЛЕ проверки «уже идёт»: предполёт может
/// ждать холодную модель до полутора минут, и всё это время окно для дубля оставалось бы
/// открытым (issue #801);</item>
/// <item><c>confirm</c> при ручной правке разбиения — отказ, пока не подтвердили. Единственное
/// место, где запустивший распознавание способен молча стереть чужую работу.</item>
/// </list>
///
/// Отказы приходят исключениями (<c>ConflictException</c>, <c>InvalidRequestException</c>,
/// <c>NotFoundException</c>) — их мапит каждый адаптер по-своему: HTTP в коды, MCP в текст ошибки.
/// </summary>
public interface IOperationLauncher
{
    /// <summary>
    /// Ставит сборку всего комплекта (или подмножества документов) в фоновую задачу.
    /// <c>null</c> — комплекта нет.
    /// </summary>
    /// <exception cref="Domain.Common.ConflictException">Сборка этого комплекта уже идёт.</exception>
    Task<Guid?> AssembleDocumentSetAsync(
        Guid setId, Guid userId, IReadOnlyList<Guid>? instanceIds, CancellationToken ct);

    /// <summary>Распознавание PDF-НАБОРА (все профили, вход по файлу). <c>null</c> — набора нет.</summary>
    Task<RecognitionLaunch?> RecognizeFileAsync(Guid fileId, Guid userId, bool confirm, CancellationToken ct);

    /// <summary>Распознавание одного ИСТОЧНИКА набора. <c>null</c> — источника нет.</summary>
    Task<RecognitionLaunch?> RecognizeSourceAsync(Guid sourceId, Guid userId, bool confirm, CancellationToken ct);
}
