import { useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ArrowUpRight, ChartNoAxesColumn, History } from 'lucide-react';
import { EmptyState } from '@/shared/ui/EmptyState';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Select, SelectItem } from '@/shared/ui/Select';
import { RevealItem, SiteSelectItems } from '@/shared/siteFilter/SiteOptions';
import { REVEAL_CHOICE, useSiteChoices } from '@/shared/siteFilter/siteReveal';
import { useSiteFilterState } from '@/shared/siteFilter/siteFilterContext';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { apiError } from '@/shared/utils/apiError';
import { useCostsConstructions } from '@/shared/api/invoices';
import { useSiteCosts, type CostCorrections, type CostFigure, type SiteCosts } from '@/shared/api/siteCosts';
import { formatCount, formatMoney } from '@/shared/format/format';
import { foldSites, foldedText, invoicesText, siteCostsLinks, type FoldedSites } from './siteCosts';
import { PageHeader } from '@/shared/ui/PageHeader';
import { COST_VIEWS_ID, CostViewsBand } from './CostViewsBand';

const ALL = 'all';

/**
 * «Затраты по стройке» (задача G5, issue #1098, ТЗ COST-20).
 *
 * Тонкий отчёт: только свёрнутые числа. Каждое ведёт в «Реестр счетов» с готовым отбором, и число
 * отчёта равно итогу «Суммы» там — основания читают в реестре, а не во втором списке здесь.
 *
 * Три вещи стоят так, чтобы их нельзя было принять за затраты:
 * «к оплате» — отдельным блоком под своим заголовком, и суммы «затраты + к оплате» нет нигде;
 * «без позиции номенклатуры» — подстрокой итога со словами «из них», а не слагаемым;
 * накладные, которых в модуле ещё нет, — подписью под заголовком, а не строкой с нулём: ноль читался
 * бы как «по накладным затрат нет».
 *
 * Состояние — в адресе страницы: стройка, период, режим НДС и взгляд на закрытые периоды переживают
 * перезагрузку и «назад» из реестра.
 */
export function SiteCostsPage() {
  useDocumentTitle('Затраты по стройке');
  const [params, setParams] = useSearchParams();
  const site = params.get('site');
  const withVat = params.get('vat') !== 'without';
  // Срез затрат стройки: по контрагентам либо по разделам (G5b, issue #1198). В адресе — как всё остальное.
  const bySections = site !== null && params.get('by') === 'sections';
  // Взгляд «на момент закрытия» (E2, PR 6b-3) — только словом в адресе: умолчание — «с исправлениями».
  const closing = params.get('view') === 'closing';
  const query = { site, from: params.get('from'), to: params.get('to'), withVat, closing };

  const sites = useCostsConstructions();
  // Выбор стройки урезан избранным, выход — последним пунктом (issue #1219). Сам отчёт считает все.
  const choices = useSiteChoices(sites.data, site);
  const report = useSiteCosts(query);
  const data = report.data;

  /** Параметр в адрес — заменой записи истории: «назад» уводит с отчёта, а не листает его настройки. */
  const set = (changes: Record<string, string | null>) => setParams(prev => {
    const next = new URLSearchParams(prev);
    for (const [key, value] of Object.entries(changes)) {
      if (value === null) next.delete(key); else next.set(key, value);
    }
    return next;
  }, { replace: true });

  // Период в полях — тот, что посчитал сервер: без параметров это текущий месяц компании, и назвать
  // его может только он («сегодня» у компании своё).
  const from = query.from ?? data?.from ?? '';
  const to = query.to ?? data?.to ?? from;

  return (
    <div className="h-full overflow-auto">
      <div className="max-w-3xl px-6 py-5 space-y-4">
        <PageHeader icon={<ChartNoAxesColumn size={18} aria-hidden />} title="Затраты по стройке"
          subtitle="Отчёт построен по счетам на оплату. Расходные накладные в него пока не входят." />

        <div className="flex flex-wrap items-end gap-3">
          {/* Подпись над полем — своя, а не рамочная у Select: рядом стоят поля месяца той же высоты. */}
          <div>
            <span className="block text-xs text-fg3 mb-1">Стройка</span>
            <Select aria-label="Стройка" className="w-64 h-8 rounded-md border border-stroke bg-surface px-2 text-sm"
              value={site ?? ALL} stayOpenOn={REVEAL_CHOICE} onValueChange={value => value === REVEAL_CHOICE ? choices.reveal()
                : set({ site: value === ALL ? null : value })}>
              <SelectItem value={ALL}>Все стройки</SelectItem>
              <SiteSelectItems choices={choices} />
              <RevealItem hidden={choices.hidden} />
            </Select>
          </div>

          <fieldset className="flex items-end gap-1.5">
            <legend className="sr-only">Учётный период</legend>
            <MonthField label="Учётный период, с" value={from}
              onChange={value => set({ from: value, to: value > to ? value : to })} />
            <MonthField label="по" value={to}
              onChange={value => set({ to: value, from: value < from ? value : from })} />
          </fieldset>

          <Segmented label="Суммы" value={withVat} options={[['с НДС', true], ['без НДС', false]]}
            onChange={value => set({ vat: value ? null : 'without' })} />

          {/* Только у стройки: раздел без стройки ничего не значит, и по всем стройкам среза нет. */}
          {site !== null && (
            <Segmented label="Срез" value={bySections} options={[['по контрагентам', false], ['по разделам', true]]}
              onChange={value => set({ by: value ? 'sections' : null })} />
          )}
        </div>

        {report.isError ? (
          <p role="alert" className="text-sm text-danger">{apiError(report.error, 'Отчёт не построился')}</p>
        ) : !data ? (
          <p className="text-sm text-fg4">Отчёт строится…</p>
        ) : (
          <Report data={data} bySections={bySections} onSite={id => set({ site: id })} stale={report.isPlaceholderData}
            asked={closing} onView={view => set({ view: view === 'closing' ? 'closing' : null })} />
        )}
      </div>
    </div>
  );
}

function Report({ data, bySections, onSite, stale, asked, onView }: {
  data: SiteCosts;
  /** Затраты стройки — по разделам, а не по контрагентам: та же сумма, сложенная иначе. */
  bySections: boolean;
  onSite: (id: string) => void;
  stale: boolean;
  /** Взгляд, названный адресом: по нему выбрана вкладка, пока ответ на него в пути. */
  asked: boolean;
  onView: (view: 'corrected' | 'closing') => void;
}) {
  const period = data.from === data.to ? data.months[0] : `${data.months[0]} — ${data.months[data.months.length - 1]}`;
  const empty = data.total.invoices === 0;
  // Неизбранные стройки — одной строкой (issue #1223): фильтр прячет строки, но чисел не меняет. Раскрытие —
  // здесь и только здесь: глобальный фильтр оно не трогает и не запоминается (§62.3 Дизайнера).
  const filter = useSiteFilterState();
  const [unfolded, setUnfolded] = useState(false);
  // Раскрытое — про ЭТОТ отчёт: другой период, НДС, стройка или избранное приходят снова свёрнутыми
  // (ревью PR #1339). Сброс — по смене того, что показано, а не по перечитыванию тех же чисел.
  const screen = [data.site?.id, data.from, data.to, data.withVat, filter.active, [...filter.favorites].sort().join()].join('|');
  const [shownScreen, setShownScreen] = useState(screen);
  if (shownScreen !== screen) { setShownScreen(screen); setUnfolded(false); }
  const { shown, folded } = foldSites(data.sites, id => filter.visible(id));
  const foldedNow = unfolded ? null : folded;
  // Под «без НДС» реестр по ссылке покажет суммы С НДС — без оговорки сверка глазами дала бы расхождение.
  const linkNote = data.withVat ? undefined : 'в реестре суммы с НДС';
  // Взгляд «на момент закрытия» — только когда он и правда другой: совпавшие взгляды полосы не рисуют, и
  // подпись о нём была бы про то, чего на экране нет. Стрелка у числа — только если реестр под ней покажет
  // то же число (сервер говорит это у каждой строки): иначе сверяли бы закрытый месяц с тем, чего в нём
  // при закрытии не было. Нет поля — старый сервер, у которого взгляд один и числа равны реестру.
  const closing = data.view === 'closing' && !!data.differs;

  const columns = costColumns(!data.site ? 'Объект' : bySections ? 'Раздел' : 'Контрагент',
    // По разделам счёт на два раздела стоит в двух строках: числа счетов в итог не складываются.
    data.site && bySections ? 'Счёт, разнесённый на несколько разделов, посчитан в каждом' : undefined, linkNote, closing);
  // Корректировки закрытых периодов — только во взгляде «на момент закрытия»: в первом их нет по устройству, а
  // при совпавших взглядах на экране числа основной таблицы.
  const adjusted = closing ? data.total.corrections : null;
  // Корректировки итога названы строкой под ним — значок у самого числа повторял бы её.
  const total: Line = { kind: 'figure', key: 'total', name: 'Итого затраты', figure: { ...data.total, corrections: null },
    link: data.totalLinked !== false ? siteCostsLinks.total(data) : undefined };
  const lines: Line[] = data.site && bySections
    // Среза может не быть в ответе: клиент и сервер обновляются порознь.
    // «Без раздела» и «раздел удалён» — приглушённо: это не название, а его отсутствие. Стрелка — только
    // под названием, каким раздел зовёт реестр: коротким именем он его не зовёт никогда, и ссылка с ним
    // открыла бы пустой реестр — «счетов нет» вместо отказа.
    ? (data.sections ?? []).map((line): Line => ({ kind: 'figure', key: line.id ?? line.name, name: line.name, muted: line.id === null,
        figure: line, link: line.linked && line.registry ? siteCostsLinks.section(data, line.registry) : undefined }))
    : data.site
    // Удалённый поставщик — без стрелки: реестр его не называет, отбора под него нет.
    ? data.suppliers.map((line): Line => ({ kind: 'figure', key: line.id ?? line.name, name: line.name, muted: line.id === null,
        figure: line, link: line.linked ? siteCostsLinks.supplier(data, line.id === null ? null : line.name) : undefined }))
    : [
        ...(data.sites.length > 0 ? [{ kind: 'group', key: 'group:sites', title: 'Стройки',
          action: unfolded && folded ? { label: 'Свернуть неизбранные', onClick: () => setUnfolded(false) } : undefined } satisfies Line] : []),
        // Первой в группе, а не последней: под ней до двадцати избранных, и в конце она ушла бы ниже
        // сгиба — а без неё столбец не сходится с итогом на глазах.
        ...(foldedNow ? [{ kind: 'folded', key: 'folded', folded: foldedNow, onUnfold: () => setUnfolded(true) } satisfies Line] : []),
        ...(foldedNow ? shown : data.sites).map((line): Line => ({ kind: 'figure', key: `site:${line.id ?? line.name}`, indent: true, figure: line,
          link: line.linked ? siteCostsLinks.object(data, line.name) : undefined, label: line.name,
          name: <button type="button" onClick={() => line.id && onSite(line.id)}
            className="text-left text-brand hover:underline">{line.name}</button> })),
        ...(data.articles.length > 0 ? [{ kind: 'group', key: 'group:articles', title: 'Вне строек' } satisfies Line] : []),
        ...data.articles.map((line): Line => ({ kind: 'figure', key: `article:${line.id ?? line.name}`, indent: true, name: line.name, figure: line,
          link: line.linked ? siteCostsLinks.object(data, line.name) : undefined })),
        // Удалённые объекты — одной строкой и без перехода «внутрь»: стройки больше нет.
        ...(data.lost ? [{ kind: 'figure', key: 'lost', name: data.lost.name, muted: true, figure: data.lost,
          link: data.lost.linked ? siteCostsLinks.object(data, data.lost.name) : undefined } satisfies Line] : []),
        // Без ссылки: отбора «есть неразнесённый остаток» у реестра нет, а «Объект: пусто» находит только
        // счета без разноски вовсе — число бы не сошлось.
        ...(data.unallocated ? [{ kind: 'figure', key: 'unallocated', name: 'Не разнесено', figure: data.unallocated } satisfies Line] : []),
      ];

  return (
    <div className={`space-y-5 ${stale ? 'opacity-60' : ''}`} aria-busy={stale}>
      <CostViewsBand data={data} closing={asked} onChange={onView} />
      <ViewPanel shown={!!data.differs} view={asked ? 'closing' : 'corrected'}>
        <section aria-label="Затраты за период">
          <h2 className="text-xs font-medium uppercase tracking-wide text-fg3">
            Затраты за {period}
            <span className="ml-2 normal-case tracking-normal font-normal text-fg4">
              оплаченные счета, по учётному периоду долей{data.withVat ? '' : ' · без НДС'}{closing ? ' · на момент закрытия' : ''}
            </span>
          </h2>

          {empty ? (
            <EmptyState icon={<ChartNoAxesColumn size={28} />} title="За период оплаченных счетов нет"
              description={data.site ? `На стройку «${data.site.name}» в эти месяцы не вошло ничего.` : 'В эти учётные месяцы не вошло ни одного платежа.'} />
          ) : (
            <DataGrid className="mt-1.5" columns={columns} rows={lines} rowKey={l => l.key} section={sectionOf}
              // Итог — главное число отчёта: им сверяют реестр. Поэтому он не мельче строк над ним и
              // выделен целиком, а не одним названием.
              footer={c => <span className="text-sm font-medium text-fg1">{c.cell?.(total, -1)}</span>} />
          )}

          {/* Месяц, где лежит разница (разбор Дизайнера, п. 7): исправление закрытого периода лежит днём
              исправления, и итог открытого месяца его уже включает. Не «из них», а «включая»: корректировка
              бывает и с минусом, и больше итога. */}
          {adjusted && (
            <p className="mt-1 pl-4 text-sm text-fg3">
              включая корректировки закрытых периодов: {invoicesText(adjusted.invoices)}, {formatMoney(adjusted.amount)}
            </p>
          )}
          {/* Во взгляде «на момент закрытия» — не «из них», а указатель (разбор Дизайнера, п. 7): «без позиции»
              — признак сегодняшний, и сумма под итогом закрытия смешивала бы два времени. Слова про взгляд —
              кнопка переключения. */}
          {closing && data.unmatchedNow && (
            <p className="mt-1 pl-4 text-sm text-fg3">
              Без позиции номенклатуры сейчас: {invoicesText(data.unmatchedNow.invoices)} —{' '}
              <button type="button" onClick={() => onView('corrected')} className="text-brand hover:underline">
                сумма и список во взгляде «с исправлениями»
              </button>
            </p>
          )}
          {/* Не слагаемые итога, а его расшифровка: «из них», с отступом и приглушённо. */}
          {!closing && data.unmatched && (
            <p className="mt-1 pl-4 text-sm text-fg3">
              из них без позиции номенклатуры: {invoicesText(data.unmatched.invoices)} на {formatMoney(data.unmatched.amount)} —
              в затраты вошли, в материалы нет{' '}
              {data.unmatchedLinked !== false && (
                <RegistryLink to={siteCostsLinks.unmatched(data)} label="Счета без позиции номенклатуры — в реестре" note={linkNote} />
              )}
            </p>
          )}
          {data.vatUnknown && (
            <p className="mt-1 pl-4 text-sm text-fg3">
              НДС не указан: {invoicesText(data.vatUnknown.invoices)} на {formatMoney(data.vatUnknown.amount)} — учтены полной суммой
            </p>
          )}
        </section>
      </ViewPanel>

      <section aria-label="Не входит в затраты" className="border-t border-stroke pt-3">
        <h2 className="text-xs font-medium uppercase tracking-wide text-fg3">Не входит в затраты</h2>
        <DataGrid className="mt-1.5" columns={costColumns('Показатель', undefined, linkNote)} rowKey={l => l.key} rows={[{
          kind: 'figure', key: 'payable', figure: data.payable, label: 'К оплате',
          link: data.payable.invoices > 0 ? siteCostsLinks.payable(data) : undefined,
          name: <>К оплате <span className="text-fg4">· не оплачено, на сегодня, от периода не зависит</span></>,
        }]} />
        {data.payableVatUnknown && (
          <p className="mt-1 pl-4 text-sm text-fg3">
            НДС не указан: {invoicesText(data.payableVatUnknown.invoices)} на {formatMoney(data.payableVatUnknown.amount)} — учтены полной суммой
          </p>
        )}
      </section>
    </div>
  );
}

/**
 * Затраты — панель выбранной вкладки взгляда, когда полоса с вкладками есть. Обёртка одна и та же в обоих
 * случаях: смена вида элемента (панель ↔ фрагмент) пересоздала бы таблицу под ней при каждой смене периода
 * с исправлениями на период без них (ревью PR #1390). Адреса — по правилу `Tabs` (`…-tab-…`, `…-panel-…`):
 * его сверяет SiteCostsViews.test — вкладка обязана указывать на эту панель.
 */
function ViewPanel({ shown, view, children }: { shown: boolean; view: string; children: ReactNode }) {
  const panel = shown
    ? { role: 'tabpanel', id: `${COST_VIEWS_ID}-panel-${view}`, 'aria-labelledby': `${COST_VIEWS_ID}-tab-${view}` }
    : {};
  return <div {...panel}>{children}</div>;
}

/** Переключатель из двух-трёх взаимоисключающих значений — кнопками, выбранная нажата. */
function Segmented<T>({ label, value, options, onChange }: {
  label: string; value: T; options: [string, T][]; onChange: (value: T) => void;
}) {
  return (
    <div role="group" aria-label={label} className="inline-flex rounded-md border border-stroke overflow-hidden">
      {options.map(([text, option]) => (
        <button key={text} type="button" aria-pressed={value === option} onClick={() => onChange(option)}
          className={`h-8 px-3 text-sm ${value === option ? 'bg-brand-subtle text-fg1 font-medium' : 'text-fg2 hover:bg-muted'}`}>
          {text}
        </button>
      ))}
    </div>
  );
}

/** Строка отчёта: заголовок группы, свёрнутые неизбранные стройки или число. */
type Line =
  | { kind: 'group'; key: string; title: string; action?: { label: string; onClick: () => void } }
  | { kind: 'folded'; key: string; folded: FoldedSites; onUnfold: () => void }
  | {
      kind: 'figure'; key: string; name: ReactNode; figure: CostFigure; link?: string; indent?: boolean; muted?: boolean;
      /** Чем строку назвать в подписи ссылки, если `name` — не текст. */
      label?: string;
    };

function sectionOf(line: Line): ReactNode | null {
  if (line.kind !== 'group') return null;
  return (
    <>
      {line.title}
      {line.action && (
        <button type="button" onClick={line.action.onClick} className="ml-2 font-normal text-fg3 underline hover:text-fg1">
          {line.action.label}
        </button>
      )}
    </>
  );
}

/** Место стрелки у строки без неё: суммы стоят столбцом, а не пляшут на ширину стрелки. */
const NO_LINK = <span className="inline-block w-[22px]" aria-hidden />;

/**
 * Корректировки закрытых периодов у строки — после названия (разбор Дизайнера, п. 7): целиком из них —
 * приглушённое слово, смешанная — значок с числами в подсказке. Не на месте стрелки, как в разборе: стрелка во
 * взгляде «на момент закрытия» остаётся у чисел, равных реестру, и смешанная строка с ней потеряла бы значок.
 */
function CorrectionMark({ corrections }: { corrections: CostCorrections | null }) {
  if (!corrections) return null;
  if (corrections.whole) return <span className="text-fg4"> · корректировка</span>;
  const title = `Включая корректировки закрытых периодов: ${invoicesText(corrections.invoices)}, ${formatMoney(corrections.amount)}`;
  return (
    <span role="img" aria-label={title} title={title} className="ml-1.5 inline-flex align-middle text-fg3">
      <History size={14} aria-hidden />
    </span>
  );
}

/**
 * Колонки отчёта: название — заголовок строки, число счетов и сумма со стрелкой в реестр.
 *
 * Неизбранные стройки — одной строкой (`folded`): название, сколько их и их деньги. Клетка «Счетов» пуста
 * нарочно — счёт на двух стройках посчитан в каждой, и число свёрнутых счетов не сложилось бы с соседними.
 * Стрелки в реестр нет: отбор «все неизбранные» — перечень названий длиной в справочник, а одноимённые
 * стройки сделали бы итог реестра больше строки.
 */
function costColumns(
  name: string, countHint: string | undefined, linkNote: string | undefined, corrections = false,
): DataGridColumn<Line>[] {
  const marked = (line: Line) => corrections && line.kind === 'figure' ? line.figure.corrections ?? null : null;
  return [
    { key: 'name', label: name, rowHeader: true, wrap: true,
      cell: line => line.kind === 'folded' ? (
        <span data-testid="site-costs-folded" className="pl-4 text-fg3">
          {foldedText(line.folded.count)}
          <button type="button" onClick={line.onUnfold} className="ml-2 text-xs underline hover:text-fg1">Показать построчно</button>
        </span>
      ) : line.kind === 'figure' ? (
        <span className={`${line.indent ? 'pl-4' : ''} ${line.muted ? 'text-fg3' : ''}`}>
          {line.name}
          <CorrectionMark corrections={marked(line)} />
        </span>
      ) : null },
    { key: 'invoices', label: 'Счетов', hint: countHint, align: 'right', width: 'w-24',
      cell: line => line.kind === 'figure' ? <span className="text-fg2">{formatCount(line.figure.invoices)}</span> : null },
    { key: 'amount', label: 'Сумма', align: 'right', width: 'w-40',
      cell: line => {
        if (line.kind === 'folded') return <span className="text-fg3">{formatMoney(line.folded.amount)}{NO_LINK}</span>;
        if (line.kind !== 'figure') return null;
        const text = line.label ?? (typeof line.name === 'string' ? line.name : '');
        return (
          <span className={line.muted ? 'text-fg3' : ''}>
            {formatMoney(line.figure.amount)}
            {line.link ? <RegistryLink to={line.link} label={`${text} — счета в реестре`} note={linkNote} /> : NO_LINK}
          </span>
        );
      } },
  ];
}


/** Стрелка в реестр: настоящая ссылка — её открывают в новой вкладке и до неё доходят клавишей Tab. */
function RegistryLink({ to, label, note }: { to: string; label: string; note?: string }) {
  const title = note ? `${label} (${note})` : label;
  return (
    <Link to={to} aria-label={title} title={title}
      className="ml-1.5 inline-flex align-middle rounded-sm text-fg3 hover:text-brand focus-visible:outline-2">
      <ArrowUpRight size={14} aria-hidden />
    </Link>
  );
}

const MONTH = /^\d{4}-(0[1-9]|1[0-2])$/;

function MonthField({ label, value, onChange }: { label: string; value: string; onChange: (value: string) => void }) {
  // Поле месяца есть не во всех браузерах: в настольных Firefox и Safari это обычная строка, и
  // человек печатает «2026-09» посимвольно. Поэтому набираемое живёт здесь, а в адрес (и на сервер)
  // уходит только готовый месяц — иначе каждый символ сменял бы отчёт отказом «месяц назван не так».
  const [draft, setDraft] = useState(value);
  const [shown, setShown] = useState(value);
  if (shown !== value) { setShown(value); setDraft(value); }

  return (
    <label className="block">
      <span className="block text-xs text-fg3 mb-1">{label}</span>
      <input type="month" value={draft} required placeholder="ГГГГ-ММ" pattern="\d{4}-(0[1-9]|1[0-2])"
        onChange={e => { setDraft(e.target.value); if (MONTH.test(e.target.value)) onChange(e.target.value); }}
        // Недописанное или стёртое — не «без периода»: отчёт без периода не строится, поле возвращает прежнее.
        onBlur={() => setDraft(value)}
        className="h-8 rounded-md border border-stroke bg-surface px-2 text-sm text-fg1 focus-visible:outline-2" />
    </label>
  );
}
