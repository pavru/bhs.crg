namespace BHS.CRG.Application.QualityDocs;

/// <param name="Code">Машинный код отказа — по нему интерфейс выбирает, что показать.</param>
/// <param name="Message">Текст для человека: что случилось и что с этим делать.</param>
public record RecognitionBlock(string Code, string Message)
{
    /// <summary>Ни один движок не участвует: не включён, не настроен либо слеп.</summary>
    public const string NoEngine = "recognition_unavailable";

    /// <summary>Все пригодные движки уличены в слепоте — распознавать некому (issue #801).</summary>
    public const string Blind = "recognition_model_blind";
}

/// <summary>
/// Можно ли вообще запускать распознавание — вопрос, который задают ДО постановки фоновой задачи
/// (issue #801).
///
/// Раньше его не задавали вовсе, и цена этого измеряется часами: альбом уходил в работу, задача
/// честно доходила до конца, а негодность движка выяснялась в лучшем случае из результата. Отказ,
/// пришедший через два часа, ничем не лучше отсутствия отказа.
///
/// Отвечает СЕРВЕР, а не клиент. Своя копия правила на клиенте разъехалась бы с этой при первом же
/// частном случае — тот же урок, по которому «чего не хватает движку» считает
/// <see cref="Settings.EngineReadiness" />, а не интерфейс.
/// </summary>
public interface IRecognitionPreflight
{
    /// <summary>Причина, по которой запускать нельзя; <c>null</c> — можно.</summary>
    Task<RecognitionBlock?> CheckAsync(CancellationToken ct = default);
}
