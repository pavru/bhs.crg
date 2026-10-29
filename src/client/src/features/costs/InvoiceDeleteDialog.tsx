import { useState, type ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useToast } from '@/shared/ui/Toast';
import { useDeleteInvoice, useFreshInvoice, type CostsOrganization, type InvoiceView } from '@/shared/api/invoices';
import { K, duplicateLabel, refEntryId, sameFileLabel } from './invoiceFields';
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
  /** Есть несохранённые правки — шапки или строк: они пропадут вместе со счётом, и диалог обязан это сказать. */
  dirty: boolean;
  /** Файл счёта сейчас читается: удалению это не мешает, но прочитанное пропадёт. */
  reading: boolean;
  organizations: CostsOrganization[];
  onDeleted: () => void;
  children: ReactNode;
}) {
  const remove = useDeleteInvoice();
  const toast = useToast();
  const readFresh = useFreshInvoice();
  // Вместе с причиной запоминается ВЕРСИЯ, которую человек видел, открывая диалог (ревью PR #1298).
  // Вид под диалогом обновляется сам — файл дочитался, сосед сохранил, — и свежая версия под прежним
  // решением удалила бы счёт, заполненный секунду назад, без единого слова.
  const [asked, setAsked] = useState<{ reason: DeleteReason; seen: string } | null>(null);

  const available = canDelete(view, canEdit);
  const title = invoiceTitle(view);
  const supplierId = refEntryId(view.requisites[K.supplier]);
  const supplier = supplierId === null ? null
    : organizations.find(o => o.id === supplierId)?.name ?? view.references?.supplierName ?? null;
  const gone = goingAway(view);

  async function confirm() {
    const seen = asked?.seen ?? view.version;
    const outcome = await deleteInvoice(() => remove.mutateAsync({ id: view.id, seen }), () => readFresh(view.id));
    if (outcome === 'deleted') toast.success(`${capital(title)} удалён.`);
    else toast.info(`${capital(title)} уже удалён.`);
    onDeleted();
  }

  return (
    <InvoiceDeleteContext.Provider value={available ? reason => setAsked({ reason, seen: view.version }) : null}>
      {children}
      <ConfirmDialog open={asked !== null} onOpenChange={open => { if (!open) setAsked(null); }}
        title={`Удалить ${title}?`}
        errorTitle="Счёт не удалён"
        confirmLabel={confirmLabel(view)} cancelLabel="Не удалять"
        description={(
          <div className="space-y-2 text-sm text-on-surface-variant" data-testid="invoice-delete-body">
            {asked?.reason === 'duplicate' && view.duplicates.length > 0 && (
              <p>Останется счёт <b>{duplicateLabel(view.duplicates[0])}</b>.</p>
            )}
            {asked?.reason === 'same-file' && view.sameFile.length > 0 && (
              <p>Останется счёт <b>{sameFileLabel(view.sameFile[0])}</b> — с тем же файлом.</p>
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
            {reading && <p>Файл сейчас распознаётся: прочитанное никуда не запишется.</p>}
            <p>
              <b>Корзины нет: вернуть счёт будет нельзя.</b> В журнале действий останется запись об
              удалении.
            </p>
            <p className="text-xs text-on-surface-variant">
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
    <div role="alert" data-testid="invoice-gone" className="flex items-start gap-2 rounded-md border
      border-error/30 bg-error-container px-3 py-2 text-xs text-on-error-container">
      <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
      <p className="flex-1">
        Этого счёта больше нет — его удалили, пока он был открыт у вас. Набранное осталось на экране:
        перенесите нужное себе. Сохранить его уже некуда.
      </p>
      <Button size="sm" variant="outlined" onClick={onClose}>Закрыть счёт</Button>
    </div>
  );
}
