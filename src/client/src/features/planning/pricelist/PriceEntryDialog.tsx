import { useState } from 'react';
import { useCan } from '@/shared/api/access';
import { useWorkTypeChoices } from '@/shared/api/planningMatches';
import { readPriceList, useWritePrice, type PriceListEntry } from '@/shared/api/planningPriceList';
import { currencySign, formatDate, formatInput } from '@/shared/format/format';
import { useLocale } from '@/shared/hooks/useLocale';
import { Banner } from '@/shared/ui/Banner';
import { DateField } from '@/shared/ui/DateField';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { NumberInput } from '@/shared/ui/NumberInput';
import { Select, SelectItem } from '@/shared/ui/Select';
import { apiError } from '@/shared/utils/apiError';
import { parseNumber } from '@/shared/utils/parseNumber';
import { moneyText } from '../offers/offerFormat';
import { CATALOG_ACCESS, hasWorkUnit, workLabel } from './workChoices';
import type { WorkTypeChoice } from '@/shared/api/planningMatches';

export type PriceEntryAction =
  | { kind: 'new'; work?: WorkTypeChoice; history?: PriceListEntry[] }
  | { kind: 'edit' | 'remove'; work: WorkTypeChoice; entry: PriceListEntry };

export function PriceEntryDialog({ action, initialDate, onClose, onReload }: {
  action: PriceEntryAction; initialDate: string; onClose: () => void; onReload: () => void;
}) {
  const can = useCan();
  const allowed = can.permission('plan.pricelist.edit');
  const canCatalog = can.permission('core.catalog.read');
  const choosing = !action.work;
  const directory = useWorkTypeChoices(choosing && allowed && canCatalog);
  const [locale] = useLocale();
  const [workId, setWorkId] = useState('');
  const original = action.kind === 'new' ? undefined : action.entry;
  const [entry, setEntry] = useState(original);
  const [date, setDate] = useState(original?.validFrom ?? initialDate);
  const [incomplete, setIncomplete] = useState(false);
  const initialText = formatInput(original?.price);
  const [text, setText] = useState(initialText);
  const [refusal, setRefusal] = useState<{ status?: number; text: string } | null>(null);
  const [reloading, setReloading] = useState(false);
  const [reviewed, setReviewed] = useState(false);
  const [gone, setGone] = useState(false);
  const write = useWritePrice();

  const choices = choosing && canCatalog && !directory.isError ? directory.data?.items.filter(hasWorkUnit) ?? [] : [];
  const work = action.work ?? choices.find(row => row.id === workId);
  const existing = !entry && action.kind === 'new' && action.history?.some(row => row.validFrom === date);
  const price = parseNumber(text, locale);
  const removing = action.kind === 'remove';
  const busy = reloading || write.isPending;
  const forbidden = !allowed || refusal?.status === 403 || choosing && !canCatalog;
  const valid = !forbidden && !gone && !existing && refusal?.status !== 409 && refusal?.status !== 404 &&
    !!work && (removing || (!!date && !incomplete && price !== null && price >= 0));

  function changeTarget(change: () => void) {
    change();
    setRefusal(null);
    setGone(false);
    setReviewed(false);
  }

  async function reload() {
    if (!work || busy || forbidden) return;
    setReloading(true);
    try {
      const fresh = await readPriceList(date);
      const sameDate = fresh.items.find(row => row.workTypeId === work.id)?.history.find(row => row.validFrom === date);
      setGone(!!entry && sameDate?.id !== entry.id);
      if (!entry || sameDate?.id === entry.id) setEntry(sameDate);
      setRefusal(null);
      setReviewed(true);
      onReload();
    } catch (error) {
      setRefusal({ status: 409, text: apiError(error, 'Не удалось перечитать цену.') });
    } finally {
      setReloading(false);
    }
  }

  async function save() {
    if (!valid || !work || busy) return;
    try {
      if (removing && entry) await write.mutateAsync({ type: 'remove', entry });
      else if (!removing && price !== null) {
        await write.mutateAsync({ type: 'save', workTypeId: work.id, price, validFrom: date, version: entry?.version });
      } else return;
      onClose();
    } catch (error) {
      const status = (error as { response?: { status?: number } })?.response?.status;
      setRefusal({ status, text: status === 403 ? 'Для изменения цен нужно право «Править прайс-лист» (plan.pricelist.edit).'
        : apiError(error, 'Изменение цены не сохранилось.') });
      setReviewed(false);
    }
  }

  return <Modal open title={removing ? 'Снять цену' : entry ? 'Изменить цену' : 'Новая цена'}
    onOpenChange={open => { if (!open) onClose(); }} busy={busy}
    isDirty={!removing && (text !== initialText || date !== (original?.validFrom ?? initialDate) || incomplete || !!workId)}
    footer={<><ModalExit>Отмена</ModalExit><ModalPrimary danger={removing} disabled={!valid}
      onClick={() => void save()}>{removing ? 'Снять цену' : 'Сохранить цену'}</ModalPrimary></>}>
    <div className="grid gap-3">
      {choosing && !canCatalog && <Banner tone="info">{CATALOG_ACCESS}</Banner>}
      {choosing && canCatalog && directory.isError && <Banner tone="error"
        actions={[{ label: 'Повторить', onClick: () => void directory.refetch() }]}>
        {apiError(directory.error, 'Классификатор видов работ не прочитался.')}
      </Banner>}
      {choosing && canCatalog && directory.data?.missing && !directory.isError && <Banner tone="info">{directory.data.missing.error}</Banner>}
      {choosing && canCatalog && !directory.isError && !directory.data?.missing &&
        <Select label="Вид работы" value={workId} disabled={busy || forbidden || !!entry || !choices.length}
          placeholder={directory.isPending ? 'Читаем классификатор…' : choices.length ? 'Выберите вид работы' : 'Нет видов работ с единицей'}
          onValueChange={id => changeTarget(() => setWorkId(id))}>
          {choices.map(row => <SelectItem value={row.id} key={row.id}>{workLabel(row)}</SelectItem>)}
        </Select>}
      {work && <p className="text-body-medium">{workLabel(work)} · {entry?.unit ?? work.unitName ?? 'без единицы'}</p>}
      {removing && entry ? <p className="text-body-medium">
        Снять цену {moneyText(entry.price)}, действующую с {formatDate(entry.validFrom)}? Сохранённые КП сохранят свои цены.
      </p> : <>
        <DateField label="Действует с" value={date} required onIncomplete={value => setIncomplete(Boolean(value))}
          onChange={value => changeTarget(() => setDate(value))} readOnly={!!entry} disabled={busy || forbidden} />
        <NumberInput label="Цена за единицу" unit={currencySign()} value={text} onChange={setText} disabled={busy || forbidden} />
      </>}
      {existing && <Banner tone="info">На эту дату уже есть цена. Чтобы изменить её, откройте запись в истории цен.</Banner>}
      {refusal && <Banner tone="error" actions={work && (refusal.status === 400 || refusal.status === 404 || refusal.status === 409) ?
        [{ label: 'Перечитать цену', onClick: () => void reload(), loading: reloading }] : undefined}>{refusal.text}</Banner>}
      {gone && <Banner tone="warning">Запись сняли или заменили. Закройте окно и откройте актуальную запись из истории.</Banner>}
      {reviewed && !gone && !refusal && <Banner tone="info">{entry
        ? `Цена в прайс-листе теперь ${moneyText(entry.price)} за ${entry.unit}. Проверьте её перед подтверждением.`
        : 'На эту дату записи пока нет. Проверьте введённую цену и сохраните её.'}</Banner>}
    </div>
  </Modal>;
}
