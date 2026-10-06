import { useState, type ReactNode } from 'react';
import { Archive, ArchiveRestore, ChevronDown, ChevronUp } from 'lucide-react';
import { Button } from '@/shared/ui/Button';

/**
 * Архивные записи в конце списка — свёрнутой строкой «В архиве: N» (issue #1185).
 *
 * Не переключатель и не «приглушённо вперемешку»: число видно всегда — существование архива не
 * спрятано, а рабочий список не засорён. `forceOpen` — когда на странице идёт поиск: совпавшие
 * архивные показываются сразу, иначе человек, искавший «Ромашку» перед «Добавить запись», завёл бы
 * дубль записи, лежащей в архиве.
 */
export function ArchivedRows({ count, forceOpen, children }: {
  count: number; forceOpen: boolean; children: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  if (count === 0) return null;
  const shown = open || forceOpen;
  return (
    <>
      <button type="button" onClick={() => setOpen(o => !o)} aria-expanded={shown} disabled={forceOpen}
        className="w-full flex items-center gap-2 px-4 py-2 border-t border-muted text-left text-xs text-fg3 enabled:hover:bg-base transition-colors">
        <Archive size={13} className="shrink-0" />
        <span className="flex-1">В архиве: {count}</span>
        {!forceOpen && (shown ? <ChevronUp size={13} className="shrink-0" /> : <ChevronDown size={13} className="shrink-0" />)}
      </button>
      {shown && children}
    </>
  );
}

/**
 * Плашка над формой архивной записи. Править её можно — реквизиты архивной организации по-прежнему
 * уходят в печать старых документов, — поэтому плашка не запирает форму, а объясняет состояние.
 */
export function ArchivedBanner({ employee, onReturn, busy }: {
  employee: boolean;
  /** Не задано — у смотрящего нет права вести общие данные (или запись ведёт модуль): кнопки нет. */
  onReturn?: () => void;
  busy: boolean;
}) {
  return (
    <div className="mx-6 mt-3 shrink-0 flex items-start gap-2.5 rounded-md border border-stroke bg-surface2 px-3 py-2.5 text-xs text-fg2">
      <Archive size={15} className="shrink-0 mt-0.5 text-fg3" />
      <p className="flex-1 min-w-0">
        Запись в архиве: в списках выбора её нет, в уже сохранённых документах и счетах она остаётся.
        {employee && ' Архив — не увольнение: дата «Уволен с» задаётся отдельным полем.'}
      </p>
      {onReturn && (
        <Button variant="tonal" size="sm" icon={<ArchiveRestore size={14} />} onClick={onReturn} disabled={busy}>
          Вернуть из архива
        </Button>
      )}
    </div>
  );
}
