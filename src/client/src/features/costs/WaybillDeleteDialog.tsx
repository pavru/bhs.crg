import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Delete } from '@/shared/ui/icons';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { useToast } from '@/shared/ui/Toast';
import { useDeleteWaybill, useFreshWaybill, type WaybillView } from '@/shared/api/waybills';
import { canDeleteWaybill, deleteWaybill, waybillConfirmLabel, waybillTitle } from './waybillDelete';

const capital = (text: string) => text[0].toUpperCase() + text.slice(1);

/**
 * Меню «Ещё» в шапке формы накладной с одним действием — «Удалить накладную…» — и его подтверждение
 * (issue #1283). Устроено как у счёта (`InvoiceDeleteDialog`), чтобы два документа спрашивали и
 * отказывали одинаково.
 *
 * <p>Пункт виден только у черновика и при праве правки: действие, которое сервер заведомо откажется
 * выполнить, не показывается вовсе. Меню без пунктов не рисуется.</p>
 *
 * <p>⚠️ Мутация не кладёт ответ в кэш и не перечитывает накладную — ответа нет и перечитывать нечего.
 * Накладную из кэша выбрасывает страница, после того как форма закрыта: выброшенная при открытой
 * форме, она была бы тут же запрошена заново и ответила бы «накладной нет».</p>
 */
export function WaybillDeleteMenu({ view, canEdit, dirty, onDeleted }: {
  view: WaybillView;
  canEdit: boolean;
  /** Есть несохранённые правки: они пропадут вместе с накладной, и диалог обязан это сказать. */
  dirty: boolean;
  onDeleted: () => void;
}) {
  const remove = useDeleteWaybill();
  const readFresh = useFreshWaybill();
  const toast = useToast();
  // Запоминается ВЕРСИЯ, которую человек видел, открывая диалог: вид под диалогом обновляется сам, и
  // свежая версия под прежним решением удалила бы накладную, изменённую секунду назад, без слова.
  const [seen, setSeen] = useState<string | null>(null);

  const title = waybillTitle(view);

  async function confirm() {
    const outcome = await deleteWaybill(
      () => remove.mutateAsync({ id: view.id, seen: seen ?? view.version }), () => readFresh(view.id));
    if (outcome === 'deleted') toast.success(`${capital(title)} удалена.`);
    else toast.info(`${capital(title)} уже удалена.`);
    onDeleted();
  }

  return (
    <>
      {canDeleteWaybill(view, canEdit) && (
        <RowActionsMenu ariaLabel="Ещё" actions={[{
          key: 'delete', label: 'Удалить накладную…', icon: <Icon icon={Delete} size={18} />, danger: true,
          onSelect: () => setSeen(view.version),
        }]} />
      )}
      <ConfirmDialog open={seen !== null} onOpenChange={open => { if (!open) setSeen(null); }}
        title={`Удалить ${title.replace('накладная', 'накладную')}?`}
        errorTitle="Накладная не удалена"
        confirmLabel={waybillConfirmLabel(view)} cancelLabel="Не удалять"
        description={(
          <div className="space-y-2 text-body-medium text-on-surface-variant" data-testid="waybill-delete-body">
            {view.lines.length > 0 && <p>Уйдут насовсем: строки накладной — {view.lines.length}.</p>}
            <p>В «Материалы на объекте» черновик не входил — там ничего не изменится.</p>
            {dirty && <p>Несохранённые правки на экране тоже пропадут.</p>}
            <p>
              <b>Корзины нет: вернуть накладную будет нельзя.</b> В журнале действий останется запись об
              удалении.
            </p>
          </div>
        )}
        onConfirm={confirm} />
    </>
  );
}
