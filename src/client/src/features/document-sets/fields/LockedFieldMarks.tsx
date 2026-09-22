import { Lock } from 'lucide-react';
import { LOCKED_HINT } from './lockedFields';

// Компоненты отдельным файлом от предиката и подсказки (та же причина, что у
// `fieldValidation.ts`, issue #858): модуль, экспортирующий и компонент, и функцию, не
// может быть границей горячей подмены — правка поднимается вверх по импортам.

/**
 * Значок замка у подписи запертого поля.
 *
 * <p>Значок, а не текст: подпись поля и так несёт звёздочку обязательности, бейджи битых ссылок и
 * расхождений, а в сетке по две колонки ширина у значения отнимается в первую очередь.</p>
 */
export function LockedFieldIcon({ className = '' }: { className?: string }) {
  return (
    // Подсказка на обёртке, а не на самой иконке: `title` у SVG-глифа браузер показывает не везде,
    // а `aria-label` слышит только вспомогательное средство — глазами подсказку тоже надо получить.
    <span title={LOCKED_HINT} aria-label={LOCKED_HINT} role="img"
      className={`inline-block shrink-0 text-fg4 ${className}`}>
      <Lock size={11} />
    </span>
  );
}

/**
 * Вид запертого поля, у которого read-only редактора нет: составного, массива, ссылки на документ.
 *
 * <p>Сегодня такое поле не заводится: объявление модуля принимает только самодостаточные виды
 * значения — строку, текст, число, дату, флаг, картинку и файл (`ModuleSystemField.Type`). Ветка
 * стоит не «на всякий случай», а потому что дыра здесь была бы ТИХОЙ: разреши белый список
 * ссылку — и поле с замком осталось бы обычным редактором, а отказ пришёл бы с сервера при
 * сохранении, без объяснения, какое из сорока полей его вызвало.</p>
 */
export function LockedFieldValue({ value }: { value: unknown }) {
  const shown = value == null || value === ''
    ? null
    : typeof value === 'object' ? JSON.stringify(value) : String(value);
  return (
    <div className="w-full border rounded-md px-3 py-2 text-sm text-fg3 bg-muted border-stroke truncate"
      title={shown ?? undefined}>
      {shown ?? <em className="text-fg4">нет значения</em>}
    </div>
  );
}
