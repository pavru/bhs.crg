import type { ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { LinkIcon, LinkOff, Refresh } from '@/shared/ui/icons';
import { SCOPE_LABELS, type FieldRef } from '@/shared/api/types';
import { SCOPE_COLORS } from './constants';
import { ArchivedRefMark } from './ArchivedRefs';
import { Tooltip } from '@/shared/ui/Tooltip';

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
    <div className="flex items-center gap-1.5 border border-outline-variant rounded-md pl-3 pr-1.5 py-1.5 bg-surface-container-low">
      <Icon icon={LinkIcon} size={18} className="text-on-surface-variant shrink-0" />
      <span className="flex-1 text-sm text-primary font-medium truncate">{value.displayName}</span>
      <ArchivedRefMark value={value} />
      {value.scope && (
        <span className={`text-xs px-2 py-0.5 rounded-full font-medium shrink-0 ${SCOPE_COLORS[value.scope]}`}>
          {SCOPE_LABELS[value.scope]}
        </span>
      )}
      <Tooltip content="Заменить ссылку" name>
        <button type="button" onClick={onReplace}
          className="p-1.5 rounded-full text-on-surface-variant hover:text-primary state-layer shrink-0">
          <Icon icon={Refresh} size={18} />
        </button>
      </Tooltip>
      <Tooltip content="Снять ссылку" name>
        <button type="button" onClick={onClear}
          className="p-1.5 rounded-full text-on-surface-variant hover:text-error state-layer shrink-0">
          <Icon icon={LinkOff} size={18} />
        </button>
      </Tooltip>
      {children}
    </div>
  );
}
