/**
 * Строка поля-массива ([UI-84], sharedui-md3-21 Р3, Р9): ссылка, битая ссылка, ссылка-вариант union и сводка
 * строки — на `ListItem`. Вынесена из `ComplexFields.tsx`: четыре ветки строк в рекурсивный круг редакторов
 * не входят.
 *
 * <p>Флажок, ручка и номер (`chrome`) собирает `ComplexFields`: они замкнуты на состояние перетаскивания, и
 * компонентом внутри его рендера их не сделать — у такого компонента каждый раз новый тип, и React размонтировал
 * бы ручку с фокусом. Приём перетаскивания (`drop`) — на обёртке: API `ListItem` под это не расширяется.</p>
 */
import type { CSSProperties, DragEvent, ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Delete, Edit, LinkIcon, Warning } from '@/shared/ui/icons';
import { IconButton } from '@/shared/ui/Button';
import { ListItem } from '@/shared/ui/ListItem';
import { Tooltip } from '@/shared/ui/Tooltip';
import type { FieldRef } from '@/shared/api/types';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { useIsArchivedRef } from './archivedRefIds';
import { BROKEN_LABEL, BrokenRefNote } from './BrokenRef';
import { overfilledNote } from './complexFieldHelpers';

/** Что показывает строка — решает `ComplexFields` (ему известны схема и пути битых ссылок). */
export type ArrayItemView =
  | { kind: 'ref'; ref: FieldRef; broken: boolean }
  /** Строка union'а, чей единственный вариант — ссылка (issue #747): тем же языком, что ссылки, плюс вариант. */
  | { kind: 'union-ref'; ref: FieldRef; label: string; broken: boolean }
  /** Сводка строки (issue #102) и заполненные варианты union'а — больше одного значит потерю данных (issue #756). */
  | { kind: 'summary'; summary: ReactNode; extraVariants: string[] };

export interface DropTarget {
  onDragOver: (e: DragEvent) => void;
  onDrop: (e: DragEvent) => void;
  style?: CSSProperties;
}

export function ArrayItemRow({ view, index, chrome, drop, checked, onEdit, onRemove }: {
  view: ArrayItemView;
  index: number;
  /** Флажок, ручка перетаскивания и номер — ведущие соседи строки. */
  chrome: ReactNode;
  drop: DropTarget;
  /** Строка отмечена флажком для удаления пачкой — заливка без `aria-current`. */
  checked: boolean;
  onEdit: () => void;
  onRemove: () => void;
}) {
  const broken = view.kind !== 'summary' && view.broken;
  // Признак архива — заранее, а не узлом `ArchivedRefMark`: пустой узел занял бы слот `trailing` с отступом.
  const archived = useIsArchivedRef(view.kind === 'summary' ? undefined : view.ref);
  const remove = <IconButton label={`Удалить строку ${index + 1}`} danger onClick={onRemove} icon={Delete}
    iconClassName={broken ? 'text-error' : undefined} />;
  // ✎ у ссылки-варианта оставлен: это единственный вход сменить вариант и снять ссылку.
  const editAndRemove = <span className="flex items-center"><IconButton label="Редактировать" onClick={onEdit} icon={Edit} />{remove}</span>;
  const lead = <span className="flex items-center gap-1">{chrome}</span>;

  if (view.kind === 'summary') {
    const many = view.extraVariants.length > 1;
    return (
      <div {...drop}>
        <ListItem leadingAction={lead} checked={checked} headline={view.summary} onClick={onEdit}
          trailing={many && (
            <Tooltip content={overfilledNote(view.extraVariants)}>
              <span className="flex items-center gap-1 text-warning" role="img" aria-label={overfilledNote(view.extraVariants)}>
                <Icon icon={Warning} size={18} /> вариантов: {view.extraVariants.length}
              </span>
            </Tooltip>
          )}
          action={editAndRemove} />
      </div>
    );
  }

  // Ссылка — строка без своей цели (`passive`): открывать у неё нечего. Битая — знак и подпись роли `error`;
  // отмеченная битая — той же заливкой, что прочие отмеченные (Р3): кольцо 1 px `primary` путалось с фокусом.
  // Приём перетаскивания — только на самой строке: пояснение под битой ссылкой целью не подсвечивается.
  return (
    <div>
      <div {...drop}>
        <ListItem passive leadingAction={lead} checked={checked} icon={LinkIcon} iconTone={broken ? 'text-error' : undefined}
          headline={broken ? <span className={BROKEN_LABEL}>{view.ref.displayName}</span> : view.ref.displayName}
          title={view.ref.displayName}
          trailing={(archived || view.kind === 'union-ref') && <>
            {archived && <ArchivedMark />}
            {view.kind === 'union-ref' && <span className="truncate max-w-40">{view.label}</span>}
          </>}
          action={view.kind === 'union-ref' ? editAndRemove : remove} />
      </div>
      {broken && <BrokenRefNote compact />}
    </div>
  );
}
