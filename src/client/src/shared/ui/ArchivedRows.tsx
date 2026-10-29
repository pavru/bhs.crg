import { useState, type ReactNode } from 'react';
import { Icon } from './Icon';
import { Archive, KeyboardArrowDown, KeyboardArrowUp } from './icons';

/**
 * Архивные записи в конце списка — свёрнутой строкой «В архиве: N» (issue #1185). Общая для
 * справочников ядра и модулей: вид архива на экранах один.
 *
 * Не переключатель и не «приглушённо вперемешку»: число видно всегда — существование архива не
 * спрятано, а рабочий список не засорён. `forceOpen` — когда на странице идёт поиск: совпавшие
 * архивные показываются сразу, иначе человек, искавший «Ромашку» перед «Добавить запись», завёл бы
 * дубль записи, лежащей в архиве.
 */
export function ArchivedRows({ count, forceOpen = false, children }: {
  count: number; forceOpen?: boolean; children: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  if (count === 0) return null;
  const shown = open || forceOpen;
  return (
    <>
      <button type="button" onClick={() => setOpen(o => !o)} aria-expanded={shown} disabled={forceOpen}
        className="w-full flex items-center gap-2 px-4 py-2 border-t border-surface-container text-left text-xs text-on-surface-variant state-layer focus-ring-inset">
        <Icon icon={Archive} size={18} className="shrink-0" />
        <span className="flex-1">В архиве: {count}</span>
        {!forceOpen && (shown ? <Icon icon={KeyboardArrowUp} size={18} className="shrink-0" /> : <Icon icon={KeyboardArrowDown} size={18} className="shrink-0" />)}
      </button>
      {shown && children}
    </>
  );
}
