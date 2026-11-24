import { WORK_SUMMARY_STATES } from '@/shared/api/work';
import { summaryStateView } from './summaryPresentation';

export function SummaryLegend() {
  return <ul aria-label="Легенда" className="flex flex-wrap gap-x-4 gap-y-2 text-body-small">
    {WORK_SUMMARY_STATES.map(code => {
      const { label, symbol, tone } = summaryStateView[code];
      return <li key={code} className="flex items-center gap-2">
        <span aria-hidden className={`grid size-6 place-items-center rounded-sm ${tone}`}>{symbol}</span>
        {label}
      </li>;
    })}
    <li className="flex items-center gap-2"><span aria-hidden className="grid size-6 place-items-center">☾</span>Ночная смена</li>
  </ul>;
}
