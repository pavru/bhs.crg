import type { ReactNode } from 'react';
import { LinkIcon } from '@/shared/ui/icons';
import { SCOPE_LABELS, type FieldRef } from '@/shared/api/types';
import { ArchivedRefMark } from './ArchivedRefs';
import { Chip } from '@/shared/ui/Chip';
import { StatusText } from '@/shared/ui/StatusText';

/**
 * Значение составного поля, заполненного ССЫЛКОЙ (issue #189) — input chip со знаком ссылки (Р4,
 * sharedui-md3-17): щелчок по чипу — заменить (открыть пикер), крестик — снять; уровень — словом рядом.
 * Вид ссылки говорит знак, а не заливка.
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
    <div className="flex flex-wrap items-center gap-2">
      <Chip icon={LinkIcon} title="Заменить ссылку" onClick={onReplace} onRemove={onClear} removeLabel="Снять ссылку"
        className="max-w-full">{value.displayName}</Chip>
      <ArchivedRefMark value={value} />
      {value.scope && <StatusText>{SCOPE_LABELS[value.scope]}</StatusText>}
      {children}
    </div>
  );
}
