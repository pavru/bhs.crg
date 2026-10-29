import { useState, type ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Checklist, SearchCheck } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { useCan } from '@/shared/api/access';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { usePurgeBatchAvailable, type PurgeBatchSource } from '@/shared/api/recordPurge';
import type { CatalogScope, DocumentType } from '@/shared/api/types';
import { PurgeBatchDialog } from './PurgeBatchDialog';

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
  const offered = allowed && !!type && (dormant.data?.available ?? dormant.isError);
  // Предел пакета — серверный. Не узнали — панель не ограничивает: откажет сам сервер, словами.
  const limit = dormant.data?.limit ?? Infinity;

  // Выбор живёт, пока открыт ЭТОТ список: тип на уровне. Ушли — режим сброшен, а не отложен
  // (ревью PR #1308): вернувшись, человек нашёл бы галочки, о которых забыл, и «Выбрано: 5» над
  // списком, где отмечать он ничего не собирался.
  const where = `${scope}|${scopeId ?? ''}|${type?.id ?? ''}`;
  const [picking, setPicking] = useState<{ where: string; ids: Set<string> } | null>(null);
  const [dialog, setDialog] = useState<PurgeBatchSource | null>(null);
  if (picking && (picking.where !== where || !offered)) setPicking(null);
  const picked = picking?.where === where && offered ? picking.ids : null;

  const toggle = (id: string) => setPicking(p => p && { ...p, ids: toggleInSet(p.ids, id) });

  const menu: ReactNode = offered && type && !picked ? (
    <RowActionsMenu ariaLabel={`Очистка: ${type.name}`} actions={[
      {
        key: 'find', label: 'Найти занятые выключенными модулями…', icon: <Icon icon={SearchCheck} size={18} />,
        onSelect: () => setDialog({ typeId: type.id, scope, scopeId }),
      },
      {
        key: 'pick', label: 'Выбрать записи для удаления с потерей ссылок', icon: <Icon icon={Checklist} size={18} />,
        onSelect: () => setPicking({ where, ids: new Set() }),
      },
    ]} />
  ) : null;

  /** Панель режима выбора — на месте кнопки «Добавить запись». `shown` — записи, видимые сейчас. */
  const bar = (shown: string[]): ReactNode => picked && type && (
    <div className="flex flex-1 items-center gap-3 flex-wrap justify-end text-body-small text-on-surface-variant">
      <span className="tabular-nums">Выбрано: {picked.size}{hiddenNote(picked, shown)}</span>
      <Button variant="text" size="sm"
        onClick={() => setPicking({ where, ids: new Set([...picked, ...shown]) })}>
        Выбрать все показанные
      </Button>
      {picked.size > limit
        ? <span className="text-warning">За раз — не больше {limit}: снимите лишние.</span>
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

/**
 * Отмеченные записи, которых сейчас не видно (скрыты поиском), — числом рядом с «Выбрано». В пакет
 * они войдут, и молчать о них нельзя: человек видит три галочки, а удалил бы восемь.
 */
function hiddenNote(picked: ReadonlySet<string>, shown: string[]): string {
  const visible = new Set(shown);
  const hidden = [...picked].filter(id => !visible.has(id)).length;
  return hidden > 0 ? ` (из них не показано: ${hidden})` : '';
}
