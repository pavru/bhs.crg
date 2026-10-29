import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { ArrowOutward, BarChart, HistoryIcon } from '@/shared/ui/icons';
import { EmptyState } from '@/shared/ui/EmptyState';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Select, SelectItem } from '@/shared/ui/Select';
import { RevealItem, SiteSelectItems } from '@/shared/siteFilter/SiteOptions';
import { REVEAL_CHOICE, useSiteChoices } from '@/shared/siteFilter/siteReveal';
import { useSiteFilterState } from '@/shared/siteFilter/siteFilterContext';
import { SitesFilter } from '@/shared/siteFilter/SitesFilter';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { apiError } from '@/shared/utils/apiError';
import { useCostsConstructions } from '@/shared/api/invoices';
import { useSiteCosts, type CostCorrections, type CostFigure, type SiteCosts } from '@/shared/api/siteCosts';
import { formatCount, formatMoney } from '@/shared/format/format';
import { correctionsText, foldSites, foldedText, invoicesText, siteCostsLinks, type FoldedSites } from './siteCosts';
import { PageHeader } from '@/shared/ui/PageHeader';
import { COST_VIEWS_ID, CostViewsBand } from './CostViewsBand';
import { OUTSIDE_SITES } from './places';
import { Tooltip } from '@/shared/ui/Tooltip';
import { DateRange } from '@/shared/ui/DateRange';

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

  /**
   * Параметр в адрес — заменой записи истории: «назад» уводит с отчёта, а не листает его настройки.
   * Следующая запись строится на предыдущей, а не на адресе отрисовки: уход из поля периода поправляет
   * границы, и щелчок «без НДС» в том же событии иначе вернул бы прежний период (issue #1466) — `prev`
   * у `setParams` — адрес на момент вызова, записи одного такта он не копит.
   */
  const written = useRef(params);
  useEffect(() => { written.current = params; }, [params]);
  const set = (changes: Record<string, string | null>) => {
    const next = new URLSearchParams(written.current);
    for (const [key, value] of Object.entries(changes)) {
      if (value === null) next.delete(key); else next.set(key, value);
    }
    written.current = next;
    setParams(next, { replace: true });
  };

  // Период в полях — тот, что посчитал сервер: без параметров это текущий месяц компании, и назвать
  // его может только он («сегодня» у компании своё).
  const from = query.from ?? data?.from ?? '';
  const to = query.to ?? data?.to ?? from;

  return (
    <div className="h-full overflow-auto">
      <div className="max-w-3xl px-6 py-5 space-y-4">
        <PageHeader icon={<Icon icon={BarChart} size={18} aria-hidden />} title="Затраты по стройке"
          subtitle="Отчёт построен по счетам на оплату. Расходные накладные в него пока не входят." />

        <div className="flex flex-wrap items-end gap-3">
          {/* Подпись над полем — своя, а не рамочная у Select: рядом период той же высоты, и подпись у него
              та же — legend `DateRange` размера md. */}
          <div>
            <span className="block mb-1 text-sm font-medium text-on-surface">Стройка</span>
            <Select aria-label="Стройка" className="w-64 h-8 rounded-sm border border-outline-variant bg-surface px-2 text-sm"
              value={site ?? ALL} stayOpenOn={REVEAL_CHOICE} onValueChange={value => value === REVEAL_CHOICE ? choices.reveal()
                : set({ site: value === ALL ? null : value })}>
              <SelectItem value={ALL}>Все стройки</SelectItem>
              <SiteSelectItems choices={choices} />
              <RevealItem hidden={choices.hidden} />
            </Select>
          </div>

          {/* Отбор «Стройки» (#1432) — у отчёта по всем стройкам: неизбранные он сворачивает в строку
              «Скрыто фильтром», и здесь же фильтр выключают для всех списков разом. У одной стройки
              сворачивать нечего. */}
          {site === null && <div className="h-8 flex items-center"><SitesFilter /></div>}

          <MonthRange from={from} to={to} onChange={(f, t) => set({ from: f, to: t })} />

          <Segmented label="Суммы" value={withVat} options={[['с НДС', true], ['без НДС', false]]}
            onChange={value => set({ vat: value ? null : 'without' })} />

          {/* Только у стройки: раздел без стройки ничего не значит, и по всем стройкам среза нет. */}
          {site !== null && (
            <Segmented label="Срез" value={bySections} options={[['по контрагентам', false], ['по разделам', true]]}
              onChange={value => set({ by: value ? 'sections' : null })} />
          )}
        </div>

        {report.isError ? (
          <p role="alert" className="text-sm text-error">{apiError(report.error, 'Отчёт не построился')}</p>
        ) : !data ? (
          <p className="text-sm text-on-surface-variant">Отчёт строится…</p>
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
  // Ноль счетов с корректировкой — не «счетов нет»: свои деньги периода и корректировка одного счёта погасили
  // друг друга (ревью PR #1395). Пустота тогда названа так, чтобы строка «включая» под ней ей не противоречила.
  const empty = data.total.invoices === 0;
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
            className="text-left text-primary hover:underline">{line.name}</button> })),
        ...(data.articles.length > 0 ? [{ kind: 'group', key: 'group:articles', title: OUTSIDE_SITES } satisfies Line] : []),
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
          <h2 className="text-xs font-medium uppercase tracking-wide text-on-surface-variant">
            Затраты за {period}
            <span className="ml-2 normal-case tracking-normal font-normal text-on-surface-variant">
              оплаченные счета, по учётному периоду долей{data.withVat ? '' : ' · без НДС'}{closing ? ' · на момент закрытия' : ''}
            </span>
          </h2>

          {empty ? (
            adjusted ? (
              <EmptyState icon={<Icon icon={BarChart} size={40} />} title="Затраты за период — ноль"
                description="Деньги, вошедшие в эти месяцы, и корректировки закрытых периодов в сумме дали ноль." />
            ) : (
              <EmptyState icon={<Icon icon={BarChart} size={40} />} title="За период оплаченных счетов нет"
                description={data.site ? `На стройку «${data.site.name}» в эти месяцы не вошло ничего.` : 'В эти учётные месяцы не вошло ни одного платежа.'} />
            )
          ) : (
            <DataGrid className="mt-1.5" columns={columns} rows={lines} rowKey={l => l.key} section={sectionOf}
              // Итог — главное число отчёта: им сверяют реестр. Поэтому он не мельче строк над ним и
              // выделен целиком, а не одним названием.
              footer={c => <span className="text-sm font-medium text-on-surface">{c.cell?.(total, -1)}</span>} />
          )}

          {/* Месяц, где лежит разница (разбор Дизайнера, п. 7): исправление закрытого периода лежит днём
              исправления, и итог открытого месяца его уже включает. Не «из них», а «включая»: корректировка
              бывает и с минусом, и больше итога. */}
          {adjusted && (
            <p className="mt-1 pl-4 text-sm text-on-surface-variant">
              {correctionsText(adjusted)}
            </p>
          )}
          {/* Во взгляде «на момент закрытия» — не «из них», а указатель (разбор Дизайнера, п. 7): «без позиции»
              — признак сегодняшний, и сумма под итогом закрытия смешивала бы два времени. Слова про взгляд —
              кнопка переключения. */}
          {closing && data.unmatchedNow && (
            <p className="mt-1 pl-4 text-sm text-on-surface-variant">
              Без позиции номенклатуры сейчас: {invoicesText(data.unmatchedNow.invoices)} —{' '}
              <button type="button" onClick={() => onView('corrected')} className="text-primary hover:underline">
                сумма и список во взгляде «с исправлениями»
              </button>
            </p>
          )}
          {/* Не слагаемые итога, а его расшифровка: «из них», с отступом и приглушённо. */}
          {!closing && data.unmatched && (
            <p className="mt-1 pl-4 text-sm text-on-surface-variant">
              из них без позиции номенклатуры: {invoicesText(data.unmatched.invoices)} на {formatMoney(data.unmatched.amount)} —
              в затраты вошли, в материалы нет{' '}
              {data.unmatchedLinked !== false && (
                <RegistryLink to={siteCostsLinks.unmatched(data)} label="Счета без позиции номенклатуры — в реестре" note={linkNote} />
              )}
            </p>
          )}
          {data.vatUnknown && (
            <p className="mt-1 pl-4 text-sm text-on-surface-variant">
              НДС не указан: {invoicesText(data.vatUnknown.invoices)} на {formatMoney(data.vatUnknown.amount)} — учтены полной суммой
            </p>
          )}
        </section>
      </ViewPanel>

      <section aria-label="Не входит в затраты" className="border-t border-outline-variant pt-3">
        <h2 className="text-xs font-medium uppercase tracking-wide text-on-surface-variant">Не входит в затраты</h2>
        <DataGrid className="mt-1.5" columns={costColumns('Показатель', undefined, linkNote)} rowKey={l => l.key} rows={[{
          kind: 'figure', key: 'payable', figure: data.payable, label: 'К оплате',
          link: data.payable.invoices > 0 ? siteCostsLinks.payable(data) : undefined,
          name: <>К оплате <span className="text-on-surface-variant">· не оплачено, на сегодня, от периода не зависит</span></>,
        }]} />
        {data.payableVatUnknown && (
          <p className="mt-1 pl-4 text-sm text-on-surface-variant">
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
    <div role="group" aria-label={label} className="inline-flex rounded-sm border border-outline-variant overflow-hidden">
      {options.map(([text, option]) => (
        <button key={text} type="button" aria-pressed={value === option} onClick={() => onChange(option)}
          className={`state-layer h-8 px-3 text-sm ${value === option ? 'bg-primary-container text-on-surface font-medium' : 'text-on-surface-variant'}`}>
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
        <button type="button" onClick={line.action.onClick} className="ml-2 font-normal text-on-surface-variant underline hover:text-on-surface">
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
  if (corrections.whole) return <span className="text-on-surface-variant"> · корректировка</span>;
  return <CorrectionIcon text={correctionsText(corrections)} />;
}

/** Значок корректировок; числа — в подсказке, той же формулировкой, что под итогом. */
function CorrectionIcon({ text }: { text: string }) {
  const title = text.charAt(0).toUpperCase() + text.slice(1);
  return (
    <Tooltip content={title}>
      <span role="img" aria-label={title} className="ml-1.5 inline-flex align-middle text-on-surface-variant">
        <Icon icon={HistoryIcon} size={18} aria-hidden />
      </span>
    </Tooltip>
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
        <span data-testid="site-costs-folded" className="pl-4 text-on-surface-variant">
          {foldedText(line.folded.count)}
          {corrections && line.folded.corrections !== null && <CorrectionIcon text={correctionsText({ amount: line.folded.corrections })} />}
          <button type="button" onClick={line.onUnfold} className="ml-2 text-xs underline hover:text-on-surface">Показать построчно</button>
        </span>
      ) : line.kind === 'figure' ? (
        <span className={`${line.indent ? 'pl-4' : ''} ${line.muted ? 'text-on-surface-variant' : ''}`}>
          {line.name}
          <CorrectionMark corrections={marked(line)} />
        </span>
      ) : null },
    { key: 'invoices', label: 'Счетов', hint: countHint, align: 'right', width: 'w-24',
      cell: line => line.kind === 'figure' ? <span className="text-on-surface-variant">{formatCount(line.figure.invoices)}</span> : null },
    { key: 'amount', label: 'Сумма', align: 'right', width: 'w-40',
      cell: line => {
        if (line.kind === 'folded') return <span className="text-on-surface-variant">{formatMoney(line.folded.amount)}{NO_LINK}</span>;
        if (line.kind !== 'figure') return null;
        const text = line.label ?? (typeof line.name === 'string' ? line.name : '');
        return (
          <span className={line.muted ? 'text-on-surface-variant' : ''}>
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
    <Tooltip content={title}>
      <Link to={to} aria-label={title}
        className="ml-1.5 inline-flex align-middle rounded-xs text-on-surface-variant hover:text-primary focus-visible:outline-2">
        <Icon icon={ArrowOutward} size={18} aria-hidden />
      </Link>
    </Tooltip>
  );
}

/**
 * Учётный период — `DateRange` с точностью месяца (issue #1466). В адресе месяц — ГГГГ-ММ, у поля — ГГГГ-ММ-01;
 * перевод здесь, на границе экрана: отданный в адрес ГГГГ-ММ-01 сервер отвергает «месяц назван не так».
 * Пару «наоборот» он отвергает тоже — её до фиксации держит `DateRange ordered`.
 *
 * <p>Стёртая граница в адрес не уходит: отчёт без периода не строится, и поле при уходе возвращает прежний
 * месяц. Но пустая с самого начала — отчёт не построился, а в адресе периода нет — не стёрта: набранный
 * месяц встаёт за обе границы, иначе выбрать период и переспросить отчёт было бы нечем.</p>
 */
function MonthRange({ from, to, onChange }: { from: string; to: string; onChange: (from: string, to: string) => void }) {
  return (
    <DateRange label="Учётный период" precision="month" size="md" required ordered className="w-full max-w-96"
      from={monthStart(from)} to={monthStart(to)}
      onChange={(f, t) => {
        if (!f && !t) return;
        if (!f || !t) {
          if (from && to) return;
          f = t = f || t;
        }
        onChange(monthOf(f), monthOf(t));
      }} />
  );
}

/** ГГГГ-ММ адреса → ГГГГ-ММ-01 поля и обратно. */
const monthStart = (month: string) => month ? `${month}-01` : '';
const monthOf = (iso: string) => iso.slice(0, 7);
