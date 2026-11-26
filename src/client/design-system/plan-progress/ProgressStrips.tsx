import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Icon } from '@/shared/ui/Icon';
import { ArrowForward, LinkOff, ReceiptLong, VisibilityOff } from '@/shared/ui/icons';
import { permissions, type Estimate, type ProgressSample, type Section } from './sample';

const money = new Intl.NumberFormat('ru-RU', {
  style: 'currency', currency: 'RUB', maximumFractionDigits: 0,
});

export function ProgressStrips({ sample, section, onEstimate, onDistribution }: {
  sample: ProgressSample;
  section: Section;
  onEstimate: (estimate: Estimate) => void;
  onDistribution: () => void;
}) {
  return <div className="grid gap-3">
    {sample.partial && <Banner tone="warning" className="p5-strip" data-testid="fact-warning">
      <strong className="block text-label-large">Факт неполный</strong>
      <span className="block">Диагностика по всей стройке за весь период, включая другие разделы</span>
      <span className="mt-1 flex flex-wrap gap-x-5 gap-y-1 text-on-surface-variant">
        <span>Отчётов ожидают приёмки: <b>3</b></span>
        <span>Групп исключено из-за разных позиций или разделов: <b>2</b></span>
        <span>Неподанные участники: <b>1</b></span>
      </span>
    </Banner>}
    {sample.workOff && <Banner tone="info" icon={LinkOff} className="p5-strip">Учёт работ не подключён</Banner>}
    <div className="p5-source-grid">
      {sample.plan ? <div className="p5-strip">
        <Banner tone="info">
          <strong className="block text-label-large">План по действующим редакциям</strong>
          <span>Электроснабжение · ред. 3 и Освещение · ред. 2</span>
        </Banner>
        <div className="p5-strip-footer">
          <p className="text-body-medium"><b>17 строк смет не сопоставлены</b> · по всей стройке</p>
          <div className="flex flex-wrap gap-x-2">
            {sample.estimates.map(estimate => <Button key={estimate.name} variant="text" multiline
              onClick={() => onEstimate(estimate)}>
              {estimate.name} · ред. {estimate.revision} ({estimate.unmatched})
              <Icon icon={ArrowForward} size={18} aria-hidden />
            </Button>)}
          </div>
          {section !== 'all' && <div className="border-t border-outline-variant pt-2">
            <p className="text-body-medium"><b>400 м по 3 работам не распределены</b> · план всей стройки</p>
            <Button variant="text" onClick={onDistribution}>Показать нераспределённое</Button>
          </div>}
        </div>
      </div> : <Banner tone="info" icon={VisibilityOff} className="p5-strip">
        <strong className="block text-label-large">План по смете скрыт правами</strong>
        Нужно право <code>{permissions.plan}</code>
      </Banner>}
      {sample.costs ? <div className="p5-strip">
        <Banner tone="info" icon={ReceiptLong}>
          <strong className="block text-label-large">Затраты · {section === 'all' ? 'вся стройка' : 'выбранный раздел'}</strong>
          <span className="block tabular-nums" data-testid="costs-values">
            Разнесено <b>{money.format(sample.costs.allocated)}</b> · из них оплачено <b>{money.format(sample.costs.paid)}</b>
          </span>
        </Banner>
        <div className="p5-strip-footer text-body-medium">
          <p><b>Привязано к работам · не ведётся</b></p>
          <p className="text-on-surface-variant">Разноска затрат по работам не ведётся.</p>
        </div>
      </div> : <Banner tone="info" icon={sample.costsOff ? LinkOff : VisibilityOff} className="p5-strip">
        {sample.costsOff ? 'Затраты · модуль «Счета и накладные» не подключён' : <>
          <strong className="block text-label-large">Затраты скрыты правами</strong>
          Нужно право <code>{permissions.costs}</code>
        </>}
      </Banner>}
    </div>
  </div>;
}
