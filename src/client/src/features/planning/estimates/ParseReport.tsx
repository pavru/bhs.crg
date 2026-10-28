import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { ChevronRight, Description, Info, KeyboardArrowDown, Warning, type IconDef } from '@/shared/ui/icons';
import { costHidden } from '@/shared/api/planningEstimates';
import type { EstimateImport, SkippedPosition, UnparsedPosition } from '@/shared/api/planningImports';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { formatCount, formatMoney } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';
import { countOf, importVerdict, reasonGroups, targetLabel } from './importReport';

/**
 * Отчёт разбора импорта (change `plan-09-estimate-ui`, PR 2, ТЗ PLAN-7, макет П1: окно варианта 1, отчёт из
 * варианта 2): откуда файл и куда ляжет редакция, «разобрано N из M», не разобранное — сгруппированными
 * причинами и списком по раскрытию, пометки разбора сервера. Действия — у окна: отчёт только показывает.
 *
 * Отказ выглядит отказом, а не пустым отчётом (лист 0 макета): не разобранный файл и прерванный разбор —
 * полосой отказа с причиной сервера.
 */
export function ParseReport({ report, sectionName }: { report: EstimateImport; sectionName?: (id: string) => string | undefined }) {
  const verdict = importVerdict(report);
  const closed = costHidden(report.hidden);
  const summary = report.summary;
  // «Не разобрано» сервер называет пометкой `partial` своими словами — показываем полосой ниже, с раскрытием.
  const notes = report.marks.filter(m => m.code !== 'partial' && m.code !== 'interrupted');

  return (
    <div className="space-y-3 text-sm">
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
        <dt className="text-fg3">Файл</dt>
        <dd className="text-on-surface break-all">
          {report.fileName}
          {report.kindName && <span className="text-fg3"> · {report.kindName}</span>}
          {report.programVersion && <span className="text-fg3"> · ГРАНД-Смета {report.programVersion}</span>}
          {summary?.lsrNumber && <span className="text-fg3"> · ЛСР {summary.lsrNumber}</span>}
        </dd>
        <dt className="text-fg3">Куда</dt>
        <dd className="text-on-surface">{capitalize(targetLabel(report.target, sectionName))}</dd>
      </dl>

      {verdict.kind === 'refused' && <Band tone="danger" icon={Warning}><b className="font-medium">Файл не разобран:</b> {verdict.text}</Band>}
      {/* Пометки сервера — над итогом, а не в конце: «это другой расчёт» решает, создавать ли вообще (макет 1в). */}
      {notes.map(m => (
        <Band key={m.code} tone={m.code === 'otherCalculation' ? 'warning' : 'info'}
          icon={m.code === 'notEstimate' ? Description : m.code === 'otherCalculation' ? Warning : Info}>
          {m.text}
        </Band>
      ))}

      {report.parsed !== null && report.inFile !== null && (
        <div className="space-y-1.5">
          <p className="font-medium text-on-surface">Разобрано {formatCount(report.parsed)} из {countOf(report.inFile, 'позиции', 'позиций', 'позиций')}</p>
          <div className="h-1.5 rounded-full bg-surface-container overflow-hidden" aria-hidden>
            <div className="h-full bg-primary" style={{ width: `${report.inFile > 0 ? (report.parsed / report.inFile) * 100 : 0}%` }} />
          </div>
          {summary && (
            <p className="text-xs text-fg3">
              {countOf(summary.chapters.length, 'раздел', 'раздела', 'разделов')} · в редакцию войдёт {countOf(summary.rows, 'строка', 'строки', 'строк')}
              {closed
                ? ` · итог скрыт: нужно право «${closed.name}»`
                : summary.cost?.total != null && ` · итог по разобранному ${formatMoney(summary.cost.total)}`}
            </p>
          )}
        </div>
      )}

      {report.unparsed.length > 0 && <Unparsed items={report.unparsed} />}
      {report.skipped.length > 0 && <Skipped items={report.skipped} />}
    </div>
  );
}

const capitalize = (s: string) => s.charAt(0).toLocaleUpperCase('ru') + s.slice(1);

type Tone = 'info' | 'warning' | 'danger';
const BAND: Record<Tone, string> = {
  info: 'bg-surface-container text-on-surface-variant',
  warning: 'border border-warning-border bg-warning-container text-on-surface',
  danger: 'border border-error/30 bg-error-container text-error',
};

function Band({ tone, icon, children }: { tone: Tone; icon: IconDef; children: React.ReactNode }) {
  return (
    <div role={tone === 'danger' ? 'alert' : undefined} className={`flex items-start gap-2 rounded-md px-3 py-2 ${BAND[tone]}`}>
      <Icon icon={icon} size={18} className="mt-0.5 shrink-0" aria-hidden />
      <p className="min-w-0">{children}</p>
    </div>
  );
}

/** Раскрывающийся заголовок списка: число и последствие видны свёрнутыми, сам список — по нажатию. */
function Disclosure({ tone, title, hint, children }: { tone: Tone; title: string; hint?: string; children: React.ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div className={`rounded-md ${BAND[tone]}`}>
      <button type="button" aria-expanded={open} onClick={() => setOpen(o => !o)}
        className="flex w-full items-start gap-2 px-3 py-2 text-left">
        {open ? <Icon icon={KeyboardArrowDown} size={18} className="mt-0.5 shrink-0" aria-hidden /> : <Icon icon={ChevronRight} size={18} className="mt-0.5 shrink-0" aria-hidden />}
        <span className="min-w-0">
          <span className="font-medium">{title}</span>
          {hint && <span className="block text-xs text-fg3">{hint}</span>}
        </span>
      </button>
      {open && <div className="px-3 pb-3">{children}</div>}
    </div>
  );
}

const UNPARSED_COLUMNS: DataGridColumn<UnparsedPosition>[] = [
  { key: 'place', label: 'Где в файле', wrap: true, cell: u => u.place },
  { key: 'line', label: 'Строка файла', align: 'right', width: 'w-28', cell: u => <span className="tabular-nums">{formatCount(u.line)}</span> },
  { key: 'reason', label: 'Почему', wrap: true, cell: u => u.reason },
];

function Unparsed({ items }: { items: UnparsedPosition[] }) {
  const groups = reasonGroups(items);
  return (
    <Disclosure tone="warning"
      title={`${countOf(items.length, 'позиция не разобрана', 'позиции не разобраны', 'позиций не разобраны')} — в редакцию не ${ruPlural(items.length, 'войдёт', 'войдут', 'войдут')}`}
      hint={`чаще всего: ${groups.slice(0, 3).map(g => `${g.reason.replace(/\.$/, '').toLocaleLowerCase('ru')} (${formatCount(g.count)})`).join(', ')}`}>
      <DataGrid density="compact" columns={UNPARSED_COLUMNS} rows={items} rowKey={u => `${u.line}:${u.place}:${u.reason}`} />
    </Disclosure>
  );
}

type SkippedLine = SkippedPosition & { at: number };

const SKIPPED_COLUMNS: DataGridColumn<SkippedLine>[] = [
  { key: 'place', label: 'Где в файле', wrap: true, cell: s => s.place },
  { key: 'text', label: 'Почему', wrap: true, cell: s => s.text },
];

function Skipped({ items }: { items: SkippedPosition[] }) {
  return (
    <Disclosure tone="info" title={`${countOf(items.length, 'неактивная позиция', 'неактивные позиции', 'неактивных позиций')} — ${ruPlural(items.length, 'строкой не станет', 'строками не станут', 'строками не станут')}`}
      hint="Позиции, выключенные в ГРАНД-Смете: разобраны, но в редакцию не входят">
      <DataGrid density="compact" columns={SKIPPED_COLUMNS} rows={items.map((s, at) => ({ ...s, at }))} rowKey={s => String(s.at)} />
    </Disclosure>
  );
}
