import { useRef, useState } from 'react';
import { useCan } from '@/shared/api/access';
import { useConstructionDirectory } from '@/shared/api/constructions';
import { useWorkTypeChoices } from '@/shared/api/planningMatches';
import { offerRefusal, useCreatePriceListOffer } from '@/shared/api/planningOffers';
import type { Offer, OfferVatMode } from '@/shared/api/planningOfferTypes';
import { usePriceList } from '@/shared/api/planningPriceList';
import { companyToday } from '@/shared/format/companyDay';
import { useLocale } from '@/shared/hooks/useLocale';
import { RevealItem, SiteSelectItems } from '@/shared/siteFilter/SiteOptions';
import { pickSite, REVEAL_CHOICE, useSiteChoices } from '@/shared/siteFilter/siteReveal';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { NumberInput } from '@/shared/ui/NumberInput';
import { Select, SelectItem } from '@/shared/ui/Select';
import { apiError } from '@/shared/utils/apiError';
import { parseNumber } from '@/shared/utils/parseNumber';
import { NumberField, OfferHeaderFields } from '../offers/OfferHeaderFields';
import { OfferLines } from '../offers/OfferLines';
import { OfferTotals } from '../offers/offerDisplay';
import { CATALOG_ACCESS, hasWorkUnit, pricedChoice, workLabel } from './workChoices';

interface DraftLine { key: number; workTypeId: string; volume: string }

export function PriceListOfferDialog({ onClose, onOpenOffer }: { onClose: () => void; onOpenOffer: (id: string) => void }) {
  const can = useCan();
  const canCatalog = can.permission('core.catalog.read');
  const allowed = can.permission('plan.offer.read') && can.permission('plan.offer.edit');
  const canChooseSite = can.permission('core.constructions.read');
  const canSubmit = allowed && canChooseSite;
  const constructions = useConstructionDirectory(canSubmit);
  const prices = usePriceList(undefined, canSubmit);
  const directory = useWorkTypeChoices(canSubmit && canCatalog);
  const [locale] = useLocale();
  const [today] = useState(companyToday);
  const [date, setDate] = useState(today);
  const [dateIncomplete, setDateIncomplete] = useState(false);
  const [site, setSite] = useState('');
  const [markup, setMarkup] = useState('0');
  const [mode, setMode] = useState<OfferVatMode>('none');
  const [rate, setRate] = useState('');
  const [workId, setWorkId] = useState('');
  const [lines, setLines] = useState<DraftLine[]>([]);
  const [created, setCreated] = useState<Offer | null>(null);
  const nextRow = useRef(0);
  const mutation = useCreatePriceListOffer();
  const siteChoices = useSiteChoices(canSubmit && !constructions.isError ? constructions.data : undefined, site || null);
  const catalogWorks = canSubmit && canCatalog && !directory.isError ? directory.data?.items ?? [] : [];
  const pricedWorks = canSubmit && !prices.isError ? prices.data?.items.filter(row => !row.archived).map(pricedChoice) ?? [] : [];
  const works = new Map([...catalogWorks, ...pricedWorks].filter(hasWorkUnit).map(work => [work.id, work]));
  const markupValue = parseNumber(markup, locale);
  const vatRate = mode === 'none' ? null : parseNumber(rate, locale);
  const volumes = lines.map(line => ({ workTypeId: line.workTypeId, volume: parseNumber(line.volume, locale) }));
  const refusal = mutation.isError ? offerRefusal(mutation.error, 'КП не создано.') : null;
  const disabled = mutation.isPending || !canSubmit || refusal?.kind === 'forbidden';
  const valid = !disabled && !constructions.isError && constructions.data?.some(row => row.id === site) && date && !dateIncomplete &&
    markupValue !== null && (mode === 'none' || vatRate !== null) && volumes.length > 0 &&
    volumes.every(line => works.has(line.workTypeId) && line.volume !== null && line.volume > 0);

  async function create() {
    if (!valid || markupValue === null) return;
    try {
      setCreated(await mutation.mutateAsync({
        constructionId: site, date, markup: markupValue, vatMode: mode, vatRate,
        lines: volumes.map(({ workTypeId, volume }) => ({ workTypeId, volume: volume! })),
      }));
    } catch { /* Отказ показывается в открытой форме. */ }
  }

  function addLine() {
    if (!works.has(workId)) return;
    const key = ++nextRow.current;
    setLines(previous => [...previous, { key, workTypeId: workId, volume: '' }]);
    setWorkId('');
  }

  if (created && allowed) return <Modal open wide nothingToLose title={`Создан черновик ${created.number}`}
    onOpenChange={open => { if (!open) onClose(); }}
    footer={<><ModalExit>Готово</ModalExit><ModalPrimary onClick={() => onOpenOffer(created.id)}>Открыть КП</ModalPrimary></>}>
    <div className="grid min-w-0 grid-cols-1 gap-3">
      {created.money.incomplete && <Banner tone="warning">В КП есть позиции без цены. Итог неполный.</Banner>}
      <OfferLines lines={created.lines} showCost={false} />
      <OfferTotals money={created.money} vat={created.vat} />
    </div>
  </Modal>;

  return <Modal open wide title="КП по прайс-листу" onOpenChange={open => { if (!open) onClose(); }}
    busy={mutation.isPending} isDirty={!!(site || workId || lines.length || date !== today || dateIncomplete || markup !== '0' || mode !== 'none')}
    footer={<><ModalExit>Отмена</ModalExit><ModalPrimary disabled={!valid} onClick={() => void create()}>Создать черновик</ModalPrimary></>}>
    <div className="grid min-w-0 grid-cols-1 gap-3">
      <Select label="Стройка" value={site} disabled={disabled || constructions.isPending || constructions.isError}
        placeholder="Выберите стройку" stayOpenOn={REVEAL_CHOICE} onValueChange={value => pickSite(value, siteChoices.reveal, setSite)}>
        <SiteSelectItems choices={siteChoices} />
        <RevealItem hidden={siteChoices.hidden} />
      </Select>
      {constructions.isError && <Banner tone="error"
        actions={[{ label: 'Повторить', onClick: () => void constructions.refetch() }]}>
        {apiError(constructions.error, 'Список строек не прочитался.')}
      </Banner>}
      <OfferHeaderFields date={date} setDate={setDate} onDateIncomplete={setDateIncomplete} mode={mode} setMode={setMode}
        rate={rate} setRate={setRate} disabled={disabled} />
      <NumberField label="Наценка КП" value={markup} onChange={setMarkup} disabled={disabled} />
      <h3 className="text-title-medium">Виды работ</h3>
      {!canCatalog && <Banner tone="info">{CATALOG_ACCESS} Можно выбрать позицию из прайс-листа.</Banner>}
      {canCatalog && directory.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void directory.refetch() }]}>
        {apiError(directory.error, 'Классификатор видов работ не прочитался.')}
      </Banner>}
      {canCatalog && !directory.isError && directory.data?.missing && <Banner tone="info">{directory.data.missing.error}</Banner>}
      {prices.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void prices.refetch() }]}>
        {apiError(prices.error, 'Позиции прайс-листа не прочитались.')}
      </Banner>}
      <div className="flex flex-wrap items-end gap-2">
        <div className="min-w-0 flex-1">
          <Select label="Вид работы" value={workId} disabled={disabled || works.size === 0} onValueChange={setWorkId}
            placeholder={works.size ? 'Выберите вид работы' : 'Нет доступных позиций для выбора'}>
            {[...works.values()].map(row => <SelectItem key={row.id} value={row.id}>{workLabel(row)}</SelectItem>)}
          </Select>
        </div>
        <Button variant="tonal" disabled={disabled || !works.has(workId)} onClick={addLine}>Добавить строку</Button>
      </div>
      <ol className="grid gap-2">{lines.map((line, index) => {
        const work = works.get(line.workTypeId);
        const label = work ? workLabel(work) : 'Вид работы недоступен';
        return <li key={line.key} className="grid min-w-0 grid-cols-1 gap-x-3 gap-y-1 sm:grid-cols-[minmax(0,1fr)_minmax(0,10rem)_auto] sm:items-center">
          <span className="text-body-medium">{index + 1}. {label}</span>
          <NumberInput label={`Объём строки ${index + 1}: ${label}`} unit={work?.unitName} value={line.volume} disabled={disabled || !work}
            onChange={volume => setLines(previous => previous.map(row => row.key === line.key ? { ...row, volume } : row))} />
          <Button variant="text" disabled={disabled} aria-label={`Убрать строку ${index + 1}`}
            onClick={() => setLines(previous => previous.filter(row => row.key !== line.key))}>Убрать</Button>
        </li>;
      })}</ol>
      <p className="text-body-small text-on-surface-variant">Цены и пометки на дату КП появятся после ответа сервера.</p>
      {refusal && <Banner tone="error">{refusal.message} Введённые данные остались в форме.</Banner>}
    </div>
  </Modal>;
}
