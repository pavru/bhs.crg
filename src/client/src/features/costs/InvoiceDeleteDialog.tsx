import { useState, type ReactNode } from 'react';
import { TriangleAlert } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useToast } from '@/shared/ui/Toast';
import { useDeleteInvoice, type CostsOrganization, type InvoiceView } from '@/shared/api/invoices';
import { K, duplicateLabel, refEntryId } from './invoiceFields';
import {
  InvoiceDeleteContext, canDelete, confirmLabel, deleteInvoice, goingAway, invoiceTitle,
  type DeleteReason,
} from './invoiceDelete';

const capital = (text: string) => text[0].toUpperCase() + text.slice(1);

/**
 * Удаление открытого счёта: один диалог на три входа — меню «Ещё», оговорку о дубликате и полосу
 * исхода распознавания (issue #1282).
 *
 * <p>Входы спрашивают через контекст и получают `null`, когда удалить нельзя: действие тогда не
 * показывается вовсе, а не показывается с отказом.</p>
 *
 * <p>⚠️ Мутация не строится на `useInvoiceWrite`: тот кладёт ответ в кэш и перечитывает счёт, а здесь
 * ответа нет и перечитывать нечего. Счёт из кэша выбрасывает страница — после того, как форма закрыта:
 * выброшенный при открытой форме, он был бы тут же запрошен заново и ответил бы «счёта нет».</p>
 */
export function InvoiceDeleteProvider({ view, canEdit, dirty, reading, organizations, onDeleted, children }: {
  view: InvoiceView;
  canEdit: boolean;
  /** Есть несохранённые правки шапки: они пропадут вместе со счётом, и диалог обязан это сказать. */
  dirty: boolean;
  /** Файл счёта сейчас читается: удалению это не мешает, но чтение прервётся. */
  reading: boolean;
  organizations: CostsOrganization[];
  onDeleted: () => void;
  children: ReactNode;
}) {
  const remove = useDeleteInvoice();
  const toast = useToast();
  const [asked, setAsked] = useState<DeleteReason | null>(null);

  const available = canDelete(view, canEdit);
  const title = invoiceTitle(view);
  const supplierId = refEntryId(view.requisites[K.supplier]);
  const supplier = supplierId === null ? null
    : organizations.find(o => o.id === supplierId)?.name ?? view.references?.supplierName ?? null;
  const gone = goingAway(view);

  async function confirm() {
    const outcome = await deleteInvoice(() => remove.mutateAsync({ id: view.id, seen: view.version }));
    if (outcome === 'deleted') toast.success(`${capital(title)} удалён.`);
    else toast.info(`${capital(title)} уже удалён.`);
    onDeleted();
  }

  return (
    <InvoiceDeleteContext.Provider value={available ? setAsked : null}>
      {children}
      <ConfirmDialog open={asked !== null} onOpenChange={open => { if (!open) setAsked(null); }}
        title={`Удалить ${title}?`}
        errorTitle="Счёт не удалён"
        confirmLabel={confirmLabel(view)} cancelLabel="Не удалять"
        description={(
          <div className="space-y-2 text-sm text-fg2" data-testid="invoice-delete-body">
            {asked === 'duplicate' && view.duplicates.length > 0 && (
              <p>Останется счёт <b>{duplicateLabel(view.duplicates[0])}</b>.</p>
            )}
            <p>{supplier ? <>Поставщик: <b>{supplier}</b>.</> : 'Поставщик не указан.'}</p>
            {gone.length > 0 && (
              <>
                <p>Уйдут насовсем:</p>
                <ul className="list-disc pl-5 space-y-1">{gone.map(item => <li key={item}>{item};</li>)}</ul>
              </>
            )}
            {supplierId !== null && view.lines.length > 0
              && <p>Запомненные соответствия наименований этого поставщика остаются.</p>}
            {dirty && <p>Несохранённые правки на экране тоже пропадут.</p>}
            {reading && <p>Файл сейчас распознаётся: чтение прервётся.</p>}
            <p>
              <b>Корзины нет: вернуть счёт будет нельзя.</b> В журнале действий останется запись об
              удалении.
            </p>
            <p className="text-xs text-fg3">
              Настоящий счёт, по которому не платим, не удаляют, а отклоняют — он остаётся в реестре.
            </p>
          </div>
        )}
        onConfirm={confirm} />
    </InvoiceDeleteContext.Provider>
  );
}

/**
 * «Этого счёта больше нет» — полоса над формой, чей счёт удалили, пока он был открыт (issue #1282).
 *
 * <p>Форма при этом НЕ закрывается: набранное остаётся на экране, чтобы его можно было перенести.
 * Раньше сосед увидел бы «Счёт не открылся… сам счёт в списке остался» — неправду — и потерял бы
 * набранное молча. Действий записи у такой формы нет: сохранять уже некуда.</p>
 */
export function InvoiceGoneNotice({ onClose }: { onClose: () => void }) {
  return (
    <div role="alert" data-testid="invoice-gone" className="flex items-start gap-2 rounded-lg border
      border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger">
      <TriangleAlert size={13} className="shrink-0 mt-0.5" />
      <p className="flex-1">
        Этого счёта больше нет — его удалили, пока он был открыт у вас. Набранное осталось на экране:
        перенесите нужное себе. Сохранить его уже некуда.
      </p>
      <Button size="sm" variant="outlined" onClick={onClose}>Закрыть счёт</Button>
    </div>
  );
}
