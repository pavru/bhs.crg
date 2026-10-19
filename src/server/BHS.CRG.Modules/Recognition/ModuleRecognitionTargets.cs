namespace BHS.CRG.Modules.Recognition;

/// <summary>Что модуль делает с полем профиля, которое объявил целью (issue #1483).</summary>
public enum ModuleTargetUse
{
    /// <summary>Значение ложится в поле записи с этим ключом — разбором модуля.</summary>
    Field,

    /// <summary>
    /// Служебный вход: значение модуль читает сам, полем записи оно не становится как есть. ИНН ищет
    /// организацию, «Итого без НДС» помогает понять режим НДС. Объявлен — чтобы не выглядеть «поля нет».
    /// </summary>
    Input,

    /// <summary>Поле записи есть, но распознаванием не заполняется — почему, сказано в причине.</summary>
    Unavailable,
}

/// <summary>Цель поля профиля, объявленная модулем (issue #1483, разбор Архитектора 18.10.2026, §1).</summary>
/// <param name="Key">Ключ поля записи — и ключ поля профиля, который в него ложится.</param>
/// <param name="Why">Причина для <see cref="ModuleTargetUse.Unavailable" />: её прочтёт человек.</param>
/// <param name="LegacyKeys">
/// Прежние ключи заводского профиля, которые ведут в это же поле: <c>НомерСчёта</c> → <c>Номер</c>.
/// <b>Закрытый список совместимости, а не синонимы</b>: заводской профиль не переименовывается — его
/// читают и наборы данных, где ключ — имя колонки, а правленые копии на установках держат старые
/// ключи. Новым целям прежние ключи не положены.
/// </param>
public sealed record ModuleRecognitionTarget(
    string Key,
    ModuleTargetUse Use = ModuleTargetUse.Field,
    string? Why = null,
    IReadOnlyList<string>? LegacyKeys = null)
{
    public IReadOnlyList<string> LegacyKeys { get; init; } = LegacyKeys ?? [];
}

/// <summary>
/// Куда ложится шапка профиля (issue #1483): правило «ключ поля профиля = ключ поля записи».
///
/// <para><b>Цели объявляет модуль, сопоставляет ядро, раскладывает модуль.</b> Модуль знает свои поля и
/// как их разбирать. Поля, которые заказчик ДОПИСАЛ в тип записи, знает только ядро: схему типа модулю
/// не отдают (<c>IModuleTypes</c>). Поэтому ядро объединяет объявленное с полями типа
/// <paramref name="RecordType" /> и отдаёт решённые цели портом <c>IModuleRecognitionTargets</c>.</para>
///
/// <para>Объявление проверяется при старте: тип принадлежит тому же модулю, ключ каждой цели-поля есть
/// среди его полей, ключи и прежние ключи не повторяются.</para>
/// </summary>
/// <param name="RecordType">Код СВОЕГО типа записи, в который ложится шапка.</param>
/// <param name="Fields">Цели шапки: поля модуля, служебные входы и поля, которые не заполняются.</param>
public sealed record ModuleRecognitionTargets(string RecordType, IReadOnlyList<ModuleRecognitionTarget> Fields);

/// <summary>Откуда цель.</summary>
public enum ModuleTargetOrigin
{
    /// <summary>Объявлена модулем: разбирает её модуль своим разбором.</summary>
    Module,

    /// <summary>Поле, дописанное в тип заказчиком: разбор — общий строгий по виду поля.</summary>
    Type,

    /// <summary>Служебный вход модуля (<see cref="ModuleTargetUse.Input" />).</summary>
    Input,
}

/// <summary>Решённая цель поля профиля — ответ ядра модулю (issue #1483).</summary>
/// <param name="Key">Ключ поля записи. У прежнего ключа профиля — нынешний: <c>Номер</c> для <c>НомерСчёта</c>.</param>
/// <param name="Type">
/// Вид значения поля: <c>string</c>, <c>text</c>, <c>number</c>, <c>date</c>. У поля с типом из реестра
/// — его базовый вид: ограничения (шаблон, длину, границы) проверяет охрана записи. У недоступного поля —
/// вид как в схеме.
/// </param>
/// <param name="Unavailable">Поле есть, но распознаванием не заполняется — почему; <c>null</c> — заполняется.</param>
/// <param name="Title">Подпись поля из схемы типа — для замечаний; нет поля в схеме — ключ.</param>
public sealed record ModuleFieldTarget(
    string Key, ModuleTargetOrigin Origin, string Type, string? Unavailable = null, string? Title = null);
