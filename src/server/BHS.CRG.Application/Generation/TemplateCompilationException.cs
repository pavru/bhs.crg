using BHS.CRG.Domain.Common;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Шаблон не скомпилировался (issue #1047) — отказ, который устраняет сам пользователь.
///
/// <para>Род — <see cref="InvalidRequestException" /> (400): на входе генерации пара «шаблон +
/// данные», и отвергнута она. Своё имя у типа затем, чтобы отличать этот отказ от внутренней
/// ошибки: прежде оба приходили одинаково — 500 с идентификатором запроса, — и единственный отказ
/// генерации, который можно исправить без администратора, выглядел поломкой сервера.</para>
///
/// <para>⚠️ Текст собирает <c>TypstCompileFailure</c>, и собирает его ОСТОРОЖНО: вывод компилятора
/// несёт абсолютные пути временной папки. Бросать этот тип с сырым <c>stderr</c> нельзя — текст
/// доменного отказа уходит пользователю дословно (см. <c>ApiErrorMapping</c>).</para>
/// </summary>
public sealed class TemplateCompilationException(string message) : InvalidRequestException(message);
