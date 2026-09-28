using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Охрана записи для модуля — та же, что у записей ядра (ТЗ CORE-20, issue #957).
///
/// <para>Зовётся <see cref="WriteGuard" />, а не <c>RecordWriteGuard</c> напрямую: в нём живёт
/// дешёвый выход для типов, которые охранять нечем (уровень «открытый»), и справочники схемы
/// читаются только там, где охрана действительно работает. Обойди мы его — модуль платил бы чтением
/// всех типов и всех примитивов на каждое сохранение.</para>
///
/// <para>⚠️ Отказ ловится и превращается в список. Отказ бросается наружу у путей ЯДРА, где его
/// поймает общий обработчик HTTP; модуль его класса не видит, поэтому здесь исключение разбирается
/// на находки, а решение остаётся модулю: отказать, спросить человека или дописать своё.</para>
/// </summary>
public sealed class ModuleWriteGuardPort(
    IRepository<DocumentType> types, IRepository<PrimitiveType> primitives) : IModuleWriteGuard
{
    public async Task<IReadOnlyList<ModuleWriteRefusal>> RefusalsAsync(
        Guid typeId, string? storedJson, string incomingJson, CancellationToken ct = default)
    {
        using var incoming = Parse(incomingJson, nameof(incomingJson));
        using var stored = storedJson is null ? null : Parse(storedJson, nameof(storedJson));

        // ⚠️ Неизвестный тип — ОТКАЗ, а не пустой список. Охрана ядра на отсутствующем типе молча
        // выходит: у её путей тип проверен раньше, и выход означает «охранять нечего». Здесь тот же
        // выход означал бы «разрешено» — то есть модуль сохранил бы данные, тронув запертые поля, и
        // узнал бы об этом никогда (ревью PR #1106). Отказ обязан отличаться от разрешения.
        if (await types.GetByIdAsync(typeId, ct) is null)
            throw new InvalidOperationException(
                $"Охране записи передан неизвестный тип {typeId}: проверять нечего, а пустой список " +
                "находок модуль прочитал бы как «запись разрешена».");

        try
        {
            await WriteGuard.EnsureAllowedAsync(stored, incoming, typeId, types, primitives, ct);
            return [];
        }
        catch (RecordWriteRefusedException refusal)
        {
            return [.. refusal.Details.Select(d => new ModuleWriteRefusal(d.Code, d.Path, d.Message))];
        }
    }

    /// <summary>
    /// Негодный JSON — отказ, называющий ПАРАМЕТР. Без имени сообщение платформы («'i' is an invalid
    /// start of a value») равно годится и для того, что лежит, и для того, что пришло, — а это разные
    /// неполадки: первая означает испорченные данные в базе, вторая — ошибку вызывающего.
    /// </summary>
    private static JsonDocument Parse(string json, string parameter)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Охране записи передан негодный JSON в «{parameter}».", ex);
        }
    }
}
