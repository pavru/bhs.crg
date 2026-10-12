import { useState, type ReactNode } from 'react';
import { ListChecks, SearchCheck } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { useCan } from '@/shared/api/access';
import { usePurgeBatchAvailable, type PurgeBatchSource } from '@/shared/api/recordPurge';
import type { CatalogScope, DocumentType } from '@/shared/api/types';
import { PurgeBatchDialog } from './PurgeBatchDialog';

/** Больше записей за раз сервер не принимает: удаляемое обязано быть обозримо до ввода числа. */
const LIMIT = 200;

/**
 * Пакетная очистка в списке справочника (issue #1245): меню типа, режим выбора и диалог. Вынесено
 * из `CatalogResource` целиком — тому остаётся поставить три куска на свои места.
 *
 * Действия есть только у выбранного типа, только при праве удалять с потерей ссылок и только если
 * на экземпляре вообще есть данные выключенных или снятых модулей: иначе искать нечего, а
 * невыполнимое действие на экране не стоит. ⚠️ Не удалось узнать (запрос упал) — действия ПОКАЗАНЫ:
 * спрятать их значило бы выдать сбой за «чистить нечего».
 */
export function usePurgeBatchControls({ scope, scopeId, type }: {
  scope: CatalogScope; scopeId: string | null; type: DocumentType | undefined;
}) {
  const allowed = useCan().permission('core.catalog.purge');
  const dormant = usePurgeBatchAvailable(allowed);
  const offered = allowed && !!type && (dormant.data ?? dormant.isError);

  // Выбор привязан к типу: сменили тип — режима нет, и сбрасывать его отдельно не нужно.
  const [picking, setPicking] = useState<{ typeId: string; ids: Set<string> } | null>(null);
  const [dialog, setDialog] = useState<PurgeBatchSource | null>(null);
  const picked = offered && picking?.typeId === type?.id ? picking.ids : null;

  const toggle = (id: string) => setPicking(p => {
    if (!p) return p;
    const ids = new Set(p.ids);
    if (!ids.delete(id)) ids.add(id);
    return { ...p, ids };
  });

  const menu: ReactNode = offered && type && !picked ? (
    <RowActionsMenu ariaLabel={`Очистка: ${type.name}`} actions={[
      {
        key: 'find', label: 'Найти занятые выключенными модулями…', icon: <SearchCheck size={14} />,
        onSelect: () => setDialog({ typeId: type.id, scope, scopeId }),
      },
      {
        key: 'pick', label: 'Выбрать записи для удаления с потерей ссылок', icon: <ListChecks size={14} />,
        onSelect: () => setPicking({ typeId: type.id, ids: new Set() }),
      },
    ]} />
  ) : null;

  /** Панель режима выбора — на месте кнопки «Добавить запись». `shown` — записи, видимые сейчас. */
  const bar = (shown: string[]): ReactNode => picked && type && (
    <div className="flex flex-1 items-center gap-3 flex-wrap justify-end text-xs text-fg2">
      <span className="tabular-nums">Выбрано: {picked.size}</span>
      <Button variant="text" size="sm"
        onClick={() => setPicking({ typeId: type.id, ids: new Set([...picked, ...shown]) })}>
        Выбрать все показанные
      </Button>
      {picked.size > LIMIT
        ? <span className="text-warning">За раз — не больше {LIMIT}: снимите лишние.</span>
        : picked.size > 0 && (
          <Button variant="filled" danger size="sm" onClick={() => setDialog({ ids: [...picked] })}>
            Удалить, потеряв ссылки…
          </Button>
        )}
      <Button variant="text" size="sm" onClick={() => setPicking(null)}>Отмена</Button>
    </div>
  );

  const dialogNode: ReactNode = dialog && (
    <PurgeBatchDialog source={dialog} typeName={type?.name}
      onClose={done => { setDialog(null); if (done) setPicking(null); }} />
  );

  return { picked, toggle, menu, bar, dialog: dialogNode };
}
