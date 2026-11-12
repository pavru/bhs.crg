import { useState, type ReactNode } from 'react';
import { Button } from '@/shared/ui/Button';
import { CountText } from '@/shared/ui/CountText';
import { Icon } from '@/shared/ui/Icon';
import { DragIndicator, Info, Keep, LockIcon } from '@/shared/ui/icons';
import { ListItem, LIST_CONTAINER } from '@/shared/ui/ListItem';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import { Tooltip } from '@/shared/ui/Tooltip';
import { useAccess } from '@/shared/api/access';
import { settingsNav, workNav } from '@/shared/ui/navConfig';
import { usePinnedPages } from '@/shared/ui/pinnedPagesContext';
import { MAX_PINNED, pinnedRows, rowNote, rowTitle, type PinnedRow } from '@/shared/ui/pinnedSections';

const NAV = [...workNav, ...settingsNav];

/**
 * «Закреплённые страницы» в профиле (ТЗ AUTH-16.4, AUTH-16.7; макет Р0, лист «Общее», п. 6).
 *
 * Здесь видно КАЖДОЕ закрепление, и убирает его только «Открепить»: группа в подпанели показывает
 * лишь открытое, а снятое право, выключенный модуль и убранная новой версией страница остаются строкой
 * с причиной — право вернут, модуль включат, и закрепление на месте (AUTH-16.3). Список, чистящий себя
 * сам, выглядел бы как поломка.
 *
 * Знак состояния тихий — это положение дел, а не ошибка. Порядок — перетаскиванием за ручку слева, с
 * клавиатуры — «Выше» / «Ниже»; он же порядок группы и палитры.
 */
export function PinnedSectionsSection() {
  const { list, unpin, move } = usePinnedPages();
  const { data: access } = useAccess();
  const rows = pinnedRows(list, NAV, access);
  const [dragged, setDragged] = useState<number | null>(null);

  return (
    <div className="mt-8 pt-6 border-t border-outline-variant" data-testid="pinned-pages-section">
      <h2 className="flex items-center gap-2 text-title-small text-on-surface mb-1">
        <Icon icon={Keep} size={18} className="text-on-surface-variant" /> Закреплённые страницы
        {rows.length > 0 && <CountText className="ms-auto">{rows.length} из {MAX_PINNED}</CountText>}
      </h2>
      {rows.length === 0 ? (
        <p className="text-body-medium text-on-surface-variant">
          Закрепите страницу из меню «⋮» у её пункта в боковой панели — она встанет первой группой «Закреплённые».
        </p>
      ) : (
        <>
          <p className="text-body-small text-on-surface-variant mb-3">
            Первой группой в боковой панели и в поиске. Порядок — перетаскиванием или кнопками «Выше» и «Ниже».
          </p>
          <div className={`${LIST_CONTAINER} rounded-md border border-outline-variant overflow-hidden`}>
            {rows.map((row, i) => (
              <div key={row.to} data-testid="pinned-row" data-kind={row.kind}
                className={dragged === i ? 'ring-1 ring-inset ring-primary' : ''}
                onDragOver={dragged !== null ? e => e.preventDefault() : undefined}
                onDrop={dragged !== null ? e => { e.preventDefault(); move(dragged, i); setDragged(null); } : undefined}>
                <Row row={row}
                  handle={
                    <Tooltip content="Перетащить">
                      <span className="flex items-center justify-center size-10 text-on-surface-variant cursor-grab" draggable
                        // Значение пустое: ронять в соседние поля нечего, а заглушка — страховка по спецификации.
                        onDragStart={e => { e.dataTransfer.setData('text/plain', ''); setDragged(i); }}
                        onDragEnd={() => setDragged(null)}>
                        <Icon icon={DragIndicator} size={24} />
                      </span>
                    </Tooltip>
                  }
                  actions={
                    <span className="flex items-center gap-1">
                      <MoveButtons onUp={() => move(i, i - 1)} onDown={() => move(i, i + 1)}
                        isFirst={i === 0} isLast={i === rows.length - 1}
                        upTitle={`Выше: ${rowTitle(row)}`} downTitle={`Ниже: ${rowTitle(row)}`} />
                      <Button variant="text" onClick={() => unpin(row.to)}>Открепить</Button>
                    </span>
                  } />
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

/** Строка: доступная страница — ссылка на неё; остальные — без цели, причиной под названием. */
function Row({ row, handle, actions }: { row: PinnedRow; handle: ReactNode; actions: ReactNode }) {
  const common = {
    headline: rowTitle(row),
    supporting: rowNote(row),
    leadingAction: handle,
    action: actions,
  };
  if (row.kind === 'ok') return <ListItem to={row.to} icon={Keep} {...common} />;
  // Доступ ещё не пришёл — ни замка, ни «нет доступа»: причину до ответа не знаем.
  if (row.kind === 'checking') return <ListItem passive icon={Keep} {...common} />;
  const lock = row.kind === 'permission' || row.kind === 'module-not-granted';
  return <ListItem passive icon={lock ? LockIcon : Info} {...common} />;
}

