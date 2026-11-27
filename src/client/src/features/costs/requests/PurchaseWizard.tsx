import { useState } from 'react';
import {
  purchaseRefusal, useCreatePurchase, usePurchaseEstimateItems, usePurchaseProposal,
  type EstimateProposal, type PurchaseConstruction, type PurchaseRequest, type PurchaseSource,
} from '@/shared/api/purchaseRequests';
import { Banner } from '@/shared/ui/Banner';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Modal, ModalAction, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { PurchaseLineFields } from './PurchaseLineFields';
import { confirmedLines, lineDrafts, type LineDraft } from './purchaseLineDraft';

export function PurchaseWizard({ constructions, initialSite, onClose, onCreated }: {
  constructions: PurchaseConstruction[]; initialSite: string; onClose: () => void; onCreated: (request: PurchaseRequest) => void;
}) {
  const [constructionId, setSite] = useState(initialSite);
  const [scope, setScope] = useState('items');
  const [sectionId, setSection] = useState('');
  const [picked, setPicked] = useState<string[]>([]);
  const [answer, setAnswer] = useState<EstimateProposal>();
  const [sourceUsed, setSourceUsed] = useState<PurchaseSource>();
  const [draft, setDraft] = useState<LineDraft[]>([]);
  const choices = usePurchaseEstimateItems(constructionId, scope === 'items' && !answer);
  const proposal = usePurchaseProposal();
  const create = useCreatePurchase();
  const site = constructions.find(item => item.id === constructionId);
  const discovered = choices.data;
  const source: PurchaseSource | undefined = !site ? undefined : scope === 'section'
    ? sectionId ? { constructionId, sectionId } : undefined
    : choices.data?.state === 'shown' && picked.length ? { constructionId, planItemIds: picked } : undefined;
  const lines = confirmedLines(draft);
  const busy = proposal.isPending || create.isPending;
  const error = proposal.error ?? create.error;

  async function advance() {
    if (!answer && source) {
      try {
        const result = await proposal.mutateAsync(source);
        setSourceUsed(source);
        setAnswer(result);
        setDraft(lineDrafts(result.state === 'shown' ? result.suggestion.lines : []));
      } catch { /* Сообщение показывает форма. */ }
    } else if (answer?.state === 'shown' && sourceUsed && lines) {
      try { onCreated(await create.mutateAsync({ ...sourceUsed, lines })); }
      catch { /* Черновик формы остаётся до следующего действия. */ }
    }
  }

  return <Modal open title="Новая заявка из сметы" wide busy={busy} onOpenChange={open => !open && onClose()}
    footer={<ModalButtons><ModalExit>Отмена</ModalExit>
      {answer && <ModalAction onClick={() => { setAnswer(undefined); proposal.reset(); create.reset(); }}>Назад</ModalAction>}
      <ModalPrimary onClick={() => void advance()} disabled={answer ? answer.state !== 'shown' || !lines : !source}>
        {answer ? 'Сохранить черновик' : 'Рассчитать предложение'}
      </ModalPrimary>
    </ModalButtons>}>
    <div className="grid gap-4">
      <ol aria-label="Шаги мастера" className="flex flex-wrap gap-x-4 gap-y-1 border-b border-outline-variant pb-3 text-body-small">
        <li aria-current={!answer ? 'step' : undefined} className={!answer ? 'text-primary' : 'text-on-surface-variant'}>Выбор материалов</li>
        <li aria-current={answer ? 'step' : undefined} className={answer ? 'text-primary' : 'text-on-surface-variant'}>Проверка строк</li>
      </ol>
      {error && <Banner tone="error">{purchaseRefusal(error).message}</Banner>}
      {!answer && <>
        <Select label="Стройка" disabled={busy} value={constructionId} onValueChange={id => {
          setSite(id); setSection(''); setPicked([]); proposal.reset();
        }}>
          {constructions.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
        </Select>
        {site && <>
          <Select label="Охват сметы" disabled={busy} value={scope} onValueChange={value => { setScope(value); proposal.reset(); }}>
            <SelectItem value="items">Позиции перечня</SelectItem>
            <SelectItem value="section">Раздел стройки</SelectItem>
          </Select>
          {scope === 'section' ? site.sections.length > 0
            ? <Select label="Раздел стройки" disabled={busy} value={sectionId} onValueChange={setSection}>
              {site.sections.map(section => <SelectItem key={section.id} value={section.id}>{section.name}</SelectItem>)}
            </Select>
            : <Banner tone="info">У стройки пока нет разделов. Можно выбрать позиции перечня.</Banner>
            : discovered?.state === 'hidden' ? <Banner tone="info">{discovered.message} ({discovered.permission})</Banner>
            : choices.isError ? <Banner tone="error">{purchaseRefusal(choices.error).message}</Banner>
            : choices.isPending ? <p role="status">Читаем позиции сметы…</p>
            : choices.data?.state === 'shown' && choices.data.items.length > 0
              ? <div className="grid gap-2" aria-label="Позиции перечня">
                {choices.data.items.map(item => <Checkbox key={item.id} disabled={busy}
                  label={item.name ?? `Позиция ${item.id}`}
                  description={`${site.sections.find(section => section.id === item.sectionId)?.name ?? 'Стройка в целом'}${item.unit ? ` · ${item.unit}` : ''}`}
                  checked={picked.includes(item.id)} onChange={checked =>
                    setPicked(previous => checked ? [...previous, item.id] : previous.filter(id => id !== item.id))} />)}
              </div> : <Banner tone="info">В действующих сметах нет позиций перечня с сопоставленными материалами.</Banner>}
        </>}
      </>}
      {answer?.state === 'hidden' && <Banner tone="info">{answer.message} ({answer.permission})</Banner>}
      {answer?.state === 'shown' && <>
        <p className="text-body-medium">
          {site?.name} · {'sectionId' in (sourceUsed ?? {}) ? site?.sections.find(item => item.id === sectionId)?.name
            : `Позиций перечня: ${picked.length}`}
        </p>
        <Banner tone="info">{answer.suggestion.warnings.join(' ')}</Banner>
        {(answer.suggestion.unmatchedIssued > 0 || answer.suggestion.unmatchedEstimate > 0 || answer.suggestion.undecidedEstimate > 0) &&
          <p className="text-body-small text-on-surface-variant">
            Без номенклатуры в отпуске: {answer.suggestion.unmatchedIssued}. В смете: {answer.suggestion.unmatchedEstimate}.
            {answer.suggestion.undecidedEstimate > 0 && ` Без решения о разделе: ${answer.suggestion.undecidedEstimate}.`}
          </p>}
        <PurchaseLineFields draft={draft} names={answer.nomenclatureNames} busy={busy} onChange={setDraft} />
      </>}
    </div>
  </Modal>;
}
