import type { ReactNode } from 'react';
import { Link2, RefreshCw, Unlink } from 'lucide-react';
import { SCOPE_LABELS, type FieldRef } from '@/shared/api/types';
import { SCOPE_COLORS } from './constants';
import { ArchivedRefMark } from './ArchivedRefs';

/**
 * Плитка составного поля, заполненного ССЫЛКОЙ (issue #189): нейтральный контейнер, имя — ссылка
 * primary, тональный chip уровня, два действия — «заменить» (открыть пикер) и «снять».
 *
 * <p>Ссылка на запись в архиве рисуется этой же плиткой, с пометкой после имени (issue #1185):
 * запись не потеряна и чинить нечего — пометка лишь говорит, что выбрать её заново не выйдет.
 * Красная плитка остаётся для потерянной цели.</p>
 */
export function RefTile({ value, onReplace, onClear, children }: {
  value: FieldRef; onReplace: () => void; onClear: () => void;
  /** Пикер: его держит плитка, чтобы он жил в том же узле, что и кнопка «заменить». */
  children?: ReactNode;
}) {
  return (
    <div className="flex items-center gap-1.5 border border-stroke rounded-lg pl-3 pr-1.5 py-1.5 bg-base">
      <Link2 size={16} className="text-fg4 shrink-0" />
      <span className="flex-1 text-sm text-brand font-medium truncate">{value.displayName}</span>
      <ArchivedRefMark value={value} />
      {value.scope && (
        <span className={`text-xs px-2 py-0.5 rounded-full font-medium shrink-0 ${SCOPE_COLORS[value.scope]}`}>
          {SCOPE_LABELS[value.scope]}
        </span>
      )}
      <button type="button" onClick={onReplace}
        className="p-1.5 rounded-full text-fg4 hover:text-brand hover:bg-black/5 dark:hover:bg-white/10 transition-colors shrink-0" title="Заменить ссылку">
        <RefreshCw size={14} />
      </button>
      <button type="button" onClick={onClear}
        className="p-1.5 rounded-full text-fg4 hover:text-danger hover:bg-black/5 dark:hover:bg-white/10 transition-colors shrink-0" title="Снять ссылку">
        <Unlink size={14} />
      </button>
      {children}
    </div>
  );
}
