import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete } from '@/shared/ui/icons';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Modal } from '@/shared/ui/Modal';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { formatDate } from '@/shared/format/format';
import { apiError } from '@/shared/utils/apiError';
import { useCostsOrganizations } from '@/shared/api/invoices';
import {
  useRemoveSupplierTerms, useSetSupplierTerms, useSupplierTerms, type SupplierTermsItem,
} from '@/shared/api/supplierTerms';
import { BAD_DAYS, choosable, parseDays, removeQuestion, termsSupplier } from './supplierTerms';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Условия поставщиков — отсрочка платежа (ТЗ COST-9.2; задача C4, issue #1080). Окно, а не страница, по
 * образцу окна соответствий (решение владельца 14.10.2026): условия правят между делом, со страницы
 * счетов, и возвращаются к счёту.
 *
 * <p>Отсрочку получает счёт, когда в нём выбирают поставщика; от неё правило считает срок «оплатить
 * до». <b>Заведённые счета правка условий не трогает</b> — это сказано в окне словами, иначе человек
 * ждал бы, что сроки сдвинутся.</p>
 */
export function SupplierTermsDialog({ onClose }: { onClose: () => void }) {
  const terms = useSupplierTerms();
  const organizations = useCostsOrganizations('choice');
  const save = useSetSupplierTerms();
  const remove = useRemoveSupplierTerms();
  const toast = useToast();

  const [supplierId, setSupplierId] = useState<string | null>(null);
  const [newDays, setNewDays] = useState('');
  // Набранное в строках — по поставщику; сохраняется уходом из поля или Enter.
  const [typed, setTyped] = useState<Record<string, string>>({});
  const [removingId, setRemovingId] = useState<string | null>(null);
  // Строки, чья правка в пути, — по поставщику, а не общим «что-то сохраняется» (ревью PR #1329): общий
  // признак молча глотал правку строки, сделанную, пока добавлялись новые условия. Пока своя правка в
  // пути, поле строки только читается — второе значение поверх первого ушло бы со старой версией.
  const [saving, setSaving] = useState<ReadonlySet<string>>(new Set());
  const [adding, setAdding] = useState(false);

  const items = terms.data ?? [];
  const offered = choosable(organizations.data ?? [], items);
  const parsedNew = parseDays(newDays);
  const removing = items.find(item => item.supplierId === removingId) ?? null;
  const dirty = supplierId !== null || newDays.trim() !== ''
    || items.some(item => typed[item.supplierId] !== undefined && typed[item.supplierId] !== String(item.deferralDays));

  async function add() {
    // Второй Enter или Enter и щелчок, пока первое заведение в пути, ушли бы вторым запросом без версии —
    // и получили бы 409 «записали одновременно с вами» о собственной правке (ревью PR #1329).
    if (adding || supplierId === null || typeof parsedNew !== 'number') return;
    setAdding(true);
    try {
      await save.mutateAsync({ supplierId, deferralDays: parsedNew });
      setSupplierId(null);
      setNewDays('');
      toast.success('Условия записаны. Отсрочку получат счета, в которых выберут этого поставщика.');
    } catch (e) { toast.apiError(e, 'Условия не записаны'); }
    finally { setAdding(false); }
  }

  async function change(item: SupplierTermsItem) {
    const text = typed[item.supplierId];
    if (text === undefined) return;
    const days = parseDays(text);
    // Пустое и неверное не отправляются: снять условия — корзиной, а неверное названо у поля.
    if (typeof days !== 'number' || saving.has(item.supplierId)) return;
    if (days === item.deferralDays) {
      setTyped(({ [item.supplierId]: _, ...rest }) => rest);
      return;
    }
    setSaving(set => new Set(set).add(item.supplierId));
    try {
      await save.mutateAsync({ supplierId: item.supplierId, deferralDays: days, version: item.version });
      setTyped(({ [item.supplierId]: _, ...rest }) => rest);
      toast.success('Отсрочка изменена. В уже заведённых счетах она не меняется.');
    } catch (e) { toast.apiError(e, 'Отсрочка не изменена'); }
    finally {
      setSaving(set => {
        const left = new Set(set);
        left.delete(item.supplierId);
        return left;
      });
    }
  }

  const columns: DataGridColumn<SupplierTermsItem>[] = [
    { key: 'supplier', label: 'Поставщик', wrap: true,
      cell: item => (
        <span className={item.supplierLost ? 'text-error' : 'text-on-surface-variant'}>
          {termsSupplier(item)}
          {item.supplierArchived && <ArchivedMark className="ml-1" />}
        </span>
      ) },
    { key: 'deferralDays', label: 'Отсрочка, дней', width: 'w-32',
      cell: item => {
        const text = typed[item.supplierId] ?? String(item.deferralDays);
        const days = parseDays(text);
        return (
          <Tooltip content={typeof days !== 'number' ? BAD_DAYS : undefined}>
            <input value={text} inputMode="numeric" readOnly={saving.has(item.supplierId)} aria-label={`Отсрочка, дней: ${termsSupplier(item)}`}
              aria-invalid={typeof days !== 'number' || undefined}
              onChange={e => setTyped(t => ({ ...t, [item.supplierId]: e.target.value }))}
              onBlur={() => void change(item)}
              onKeyDown={e => { if (e.key === 'Enter') void change(item); }}
              className={`w-24 rounded border bg-surface px-2 py-0.5 text-sm text-on-surface
                ${typeof days !== 'number' ? 'border-error' : 'border-outline-variant'}`} />
          </Tooltip>
        );
      } },
    { key: 'updatedAt', label: 'Изменено',
      cell: item => <span className="text-on-surface-variant">{formatDate(item.updatedAt)}{item.updatedBy ? ` · ${item.updatedBy}` : ''}</span> },
    { key: 'remove', label: '', width: 'w-8',
      cell: item => (
        <Tooltip content="Снять условия" name>
          <button type="button" disabled={saving.has(item.supplierId)}
            onClick={() => setRemovingId(item.supplierId)}
            className="text-on-surface-variant hover:text-error p-1"><Icon icon={Delete} size={18} /></button>
        </Tooltip>
      ) },
  ];

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Условия поставщиков" wide isDirty={dirty}>
      <p className="text-xs text-on-surface-variant mb-3">
        Отсрочка платежа у поставщика. Её получает счёт, когда в нём выбирают поставщика, и от неё
        считается срок «оплатить до». В уже заведённых счетах правка условий ничего не меняет.
      </p>

      {/* Новые условия — сверху: окно открывают чаще всего затем, чтобы завести их поставщику. */}
      <div className="flex flex-wrap items-end gap-2 mb-3">
        <Select label="Поставщик" value={supplierId ?? undefined} placeholder="Выберите организацию"
          containerClassName="flex-1 min-w-[14rem]"
          disabled={!organizations.data}
          hint={organizations.isError ? 'Справочник организаций не прочитан — выбор недоступен'
            : organizations.data && offered.length === 0 ? 'Условия заведены у всех организаций' : undefined}
          onValueChange={setSupplierId}>
          {offered.map(o => <SelectItem key={o.id} value={o.id}>{o.name}</SelectItem>)}
        </Select>
        <TextField label="Отсрочка, дней" value={newDays} inputMode="numeric" containerClassName="w-36"
          error={parsedNew === 'bad' ? BAD_DAYS : undefined}
          onChange={e => setNewDays(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') void add(); }} />
        <Button variant="outlined" icon={<Icon icon={Add} size={18} />} loading={adding}
          disabled={supplierId === null || typeof parsedNew !== 'number'} onClick={() => void add()}>
          Добавить
        </Button>
      </div>

      {terms.isError ? (
        <div className="rounded-lg border border-error/30 px-3 py-2 text-sm text-error">
          Условия не прочитаны: {apiError(terms.error)} Это не «условий нет».{' '}
          <button type="button" className="underline" onClick={() => void terms.refetch()}>Повторить</button>
        </div>
      ) : !terms.data ? (
        <p className="py-6 text-center text-sm text-on-surface-variant">Читаем условия…</p>
      ) : items.length === 0 ? (
        <p className="py-6 text-center text-sm text-on-surface-variant">
          Условий пока нет. Выберите поставщика и укажите отсрочку — новые счета получат её сами.
        </p>
      ) : (
        <DataGrid density="compact" className="max-h-[55vh]" columns={columns} rows={items} rowKey={item => item.supplierId} />
      )}

      <ConfirmDialog open={removingId !== null} onOpenChange={o => { if (!o) setRemovingId(null); }}
        title="Снять условия поставщика?" description={removing ? removeQuestion(removing) : undefined}
        confirmLabel="Снять" errorTitle="Условия не сняты"
        onConfirm={() => (removing
          ? remove.mutateAsync(removing).then(() => undefined)
          : Promise.reject(new Error('Этих условий в окне уже нет — их сняли или окно перечитано.')))} />
    </Modal>
  );
}
