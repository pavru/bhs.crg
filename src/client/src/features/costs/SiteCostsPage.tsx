import type { ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ArrowUpRight, ChartNoAxesColumn } from 'lucide-react';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { apiError } from '@/shared/utils/apiError';
import { useCostsConstructions } from '@/shared/api/invoices';
import { useSiteCosts, type CostFigure, type SiteCosts } from '@/shared/api/siteCosts';
import { formatMoney } from './invoiceFields';
import { invoicesText, siteCostsLinks } from './siteCosts';

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
 * Состояние — в адресе страницы: стройка, период и режим НДС переживают перезагрузку и «назад» из
 * реестра.
 */
export function SiteCostsPage() {
  useDocumentTitle('Затраты по стройке');
  const [params, setParams] = useSearchParams();
  const site = params.get('site');
  const withVat = params.get('vat') !== 'without';
  const query = { site, from: params.get('from'), to: params.get('to'), withVat };

  const sites = useCostsConstructions();
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
        <header>
          <h1 className="flex items-center gap-2 text-lg font-semibold text-fg1">
            <ChartNoAxesColumn size={18} className="text-fg3" aria-hidden />
            Затраты по стройке
          </h1>
          <p className="mt-0.5 text-sm text-fg3">
            Отчёт построен по счетам на оплату. Расходные накладные в него пока не входят.
          </p>
        </header>

        <div className="flex flex-wrap items-end gap-3">
          {/* Подпись над полем — своя, а не рамочная у Select: рядом стоят поля месяца той же высоты. */}
          <div>
            <span className="block text-xs text-fg3 mb-1">Стройка</span>
            <Select aria-label="Стройка" className="w-64 h-8 rounded-md border border-stroke bg-surface px-2 text-sm"
              value={site ?? ALL} onValueChange={value => set({ site: value === ALL ? null : value })}>
              <SelectItem value={ALL}>Все стройки</SelectItem>
              {(sites.data ?? []).map(s => <SelectItem key={s.id} value={s.id}>{s.name}</SelectItem>)}
            </Select>
          </div>

          <fieldset className="flex items-end gap-1.5">
            <legend className="sr-only">Учётный период</legend>
            <MonthField label="Учётный период, с" value={from}
              onChange={value => set({ from: value, to: value > to ? value : to })} />
            <MonthField label="по" value={to}
              onChange={value => set({ to: value, from: value < from ? value : from })} />
          </fieldset>

          <div role="group" aria-label="Суммы" className="inline-flex rounded-md border border-stroke overflow-hidden">
            {[['с НДС', true], ['без НДС', false]].map(([label, value]) => (
              <button key={String(label)} type="button" aria-pressed={withVat === value}
                onClick={() => set({ vat: value ? null : 'without' })}
                className={`h-8 px-3 text-sm ${withVat === value ? 'bg-brand-subtle text-fg1 font-medium' : 'text-fg2 hover:bg-surface2'}`}>
                {label}
              </button>
            ))}
          </div>
        </div>

        {report.isError ? (
          <p role="alert" className="text-sm text-danger">{apiError(report.error, 'Отчёт не построился')}</p>
        ) : !data ? (
          <p className="text-sm text-fg4">Отчёт строится…</p>
        ) : (
          <Report data={data} onSite={id => set({ site: id })} stale={report.isPlaceholderData} />
        )}
      </div>
    </div>
  );
}

function Report({ data, onSite, stale }: { data: SiteCosts; onSite: (id: string) => void; stale: boolean }) {
  const period = data.from === data.to ? data.months[0] : `${data.months[0]} — ${data.months[data.months.length - 1]}`;
  const empty = data.total.invoices === 0;
  // Под «без НДС» реестр по ссылке покажет суммы С НДС — без оговорки сверка глазами дала бы расхождение.
  const linkNote = data.withVat ? undefined : 'в реестре суммы с НДС';

  return (
    <div className={`space-y-5 ${stale ? 'opacity-60' : ''}`} aria-busy={stale}>
      <section aria-label="Затраты за период">
        <h2 className="text-xs font-medium uppercase tracking-wide text-fg3">
          Затраты за {period}
          <span className="ml-2 normal-case tracking-normal font-normal text-fg4">
            оплаченные счета, по учётному периоду долей{data.withVat ? '' : ' · без НДС'}
          </span>
        </h2>

        {empty ? (
          <EmptyState icon={<ChartNoAxesColumn size={28} />} title="За период оплаченных счетов нет"
            description={data.site ? `На стройку «${data.site.name}» в эти месяцы не вошло ничего.` : 'В эти учётные месяцы не вошло ни одного платежа.'} />
        ) : (
          <table className="mt-1.5 w-full text-sm">
            <thead>
              <tr className="text-xs text-fg4">
                <th scope="col" className="py-1 text-left font-normal">{data.site ? 'Контрагент' : 'Объект'}</th>
                <th scope="col" className="py-1 text-right font-normal w-24">Счетов</th>
                <th scope="col" className="py-1 text-right font-normal w-40">Сумма</th>
              </tr>
            </thead>
            <tbody>
              {data.site ? (
                data.suppliers.map(line => (
                  <Row key={line.id ?? 'none'} name={line.name} muted={line.id === null} figure={line}
                    link={siteCostsLinks.supplier(data, line.id === null ? null : line.name)} linkNote={linkNote} />
                ))
              ) : (
                <>
                  {data.sites.length > 0 && <Group title="Стройки" />}
                  {data.sites.map(line => (
                    <Row key={line.id} indent figure={line} link={siteCostsLinks.object(data, line.name)} linkNote={linkNote}
                      name={<button type="button" onClick={() => line.id && onSite(line.id)}
                        className="text-left text-brand hover:underline">{line.name}</button>} label={line.name} />
                  ))}
                  {data.articles.length > 0 && <Group title="Вне строек" />}
                  {data.articles.map(line => (
                    <Row key={line.id} indent name={line.name} figure={line}
                      link={siteCostsLinks.object(data, line.name)} linkNote={linkNote} />
                  ))}
                  {/* Без ссылки: отбора «есть неразнесённый остаток» у реестра нет, а «Объект: пусто»
                      находит только счета без разноски вовсе — число бы не сошлось. */}
                  {data.unallocated && <Row name="Не разнесено" figure={data.unallocated} />}
                </>
              )}
            </tbody>
            <tfoot>
              <Row name="Итого затраты" strong figure={data.total} link={siteCostsLinks.total(data)} linkNote={linkNote} />
            </tfoot>
          </table>
        )}

        {/* Не слагаемые итога, а его расшифровка: «из них», с отступом и приглушённо. */}
        {data.unmatched && (
          <p className="mt-1 pl-4 text-sm text-fg3">
            из них без позиции номенклатуры: {invoicesText(data.unmatched.invoices)} на {formatMoney(data.unmatched.amount)} —
            в затраты вошли, в материалы нет{' '}
            <RegistryLink to={siteCostsLinks.unmatched(data)} label="Счета без позиции номенклатуры — в реестре" note={linkNote} />
          </p>
        )}
        {data.vatUnknown && (
          <p className="mt-1 pl-4 text-sm text-fg3">
            НДС не указан: {invoicesText(data.vatUnknown.invoices)} на {formatMoney(data.vatUnknown.amount)} — учтены полной суммой
          </p>
        )}
      </section>

      <section aria-label="Не входит в затраты" className="border-t border-stroke pt-3">
        <h2 className="text-xs font-medium uppercase tracking-wide text-fg3">Не входит в затраты</h2>
        <table className="mt-1.5 w-full text-sm">
          <tbody>
            <Row figure={data.payable} link={data.payable.invoices > 0 ? siteCostsLinks.payable(data) : undefined} linkNote={linkNote}
              label="К оплате"
              name={<>К оплате <span className="text-fg4">· не оплачено, на сегодня, от периода не зависит</span></>} />
          </tbody>
        </table>
      </section>
    </div>
  );
}

function Group({ title }: { title: string }) {
  return <tr><th scope="rowgroup" colSpan={3} className="pt-2 pb-0.5 text-left text-xs font-medium text-fg3">{title}</th></tr>;
}

function Row({ name, label, figure, link, linkNote, indent, strong, muted }: {
  name: ReactNode;
  /** Чем строку назвать в подписи ссылки, если `name` — не текст. */
  label?: string;
  figure: CostFigure;
  link?: string;
  linkNote?: string;
  indent?: boolean;
  strong?: boolean;
  muted?: boolean;
}) {
  const text = label ?? (typeof name === 'string' ? name : '');
  return (
    <tr className={`${strong ? 'border-t border-stroke font-medium' : 'border-t border-stroke/40'} ${muted ? 'text-fg3' : 'text-fg1'}`}>
      <th scope="row" className={`py-1 text-left ${strong ? 'font-medium' : 'font-normal'} ${indent ? 'pl-4' : ''}`}>{name}</th>
      <td className="py-1 text-right tabular-nums text-fg2">{figure.invoices.toLocaleString('ru-RU')}</td>
      <td className="py-1 text-right tabular-nums whitespace-nowrap">
        {formatMoney(figure.amount)}
        {link
          ? <RegistryLink to={link} label={`${text} — счета в реестре`} note={linkNote} />
          : <span className="inline-block w-[22px]" aria-hidden />}
      </td>
    </tr>
  );
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

function MonthField({ label, value, onChange }: { label: string; value: string; onChange: (value: string) => void }) {
  return (
    <label className="block">
      <span className="block text-xs text-fg3 mb-1">{label}</span>
      <input type="month" value={value} required
        // Пустое поле месяца — не «без периода»: отчёт без периода не строится, и стирание значения
        // оставляет прежнее.
        onChange={e => { if (e.target.value) onChange(e.target.value); }}
        className="h-8 rounded-md border border-stroke bg-surface px-2 text-sm text-fg1 focus-visible:outline-2" />
    </label>
  );
}
