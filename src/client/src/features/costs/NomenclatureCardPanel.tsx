import { ExternalLink, TriangleAlert } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { useAccess, NO_ACCESS } from '@/shared/api/access';
import { useNomenclatureCard, type NomenclatureCard } from '@/shared/api/nomenclatureCard';
import { isNotFound } from '@/shared/api/invoices';
import { CATALOG_RECORD, openTo, recordLink } from '@/shared/ui/recordRoutes';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { apiError } from '@/shared/utils/apiError';
import { SCOPE_LABELS } from '@/shared/api/types';
import { cardMeta, chosenText, lineValue, onlyName, type CompareRow, type CardValueTone } from './nomenclatureCardView';

/** Что известно о позиции до прихода карточки — из строки списка: шапка не ждёт запроса. */
export interface CardHead {
  name: string | null;
  type: string;
  /** Почему позиция в списке: «найдено по «…»». */
  why?: string | null;
}

/** Уровни области с сервера — тем же письмом, что у каталога. */
const SCOPE = { construction: 'Construction', section: 'Section', set: 'Set' } as const;

const TONE: Record<CardValueTone, string> = {
  value: 'text-fg1',
  empty: 'text-fg4',
  unreadable: 'text-warning',
  quiet: 'text-fg3',
};

const COMPARE_TONE: Record<CompareRow['tone'], string> = {
  ok: 'text-success',
  differs: 'text-warning',
  plain: 'text-fg2',
};

/**
 * Карточка позиции справа от списка в окне выбора (issue #1517, данные — #1516): убедиться до выбора, что
 * позиция та. Одна на окно, показывает выделенную.
 *
 * <p>⚠️ Каждое состояние — своими словами: «ничего не выделено», «грузится», «не прочитана», «позицию
 * удалили». Пустая панель на месте любого из них читалась бы как «у позиции нет полей».</p>
 */
export function NomenclatureCardPanel({ id, head, compare, pick = 'tonal', onPick }: {
  /** Выделенная позиция; `null` — ничего не выделено. */
  id: string | null;
  head?: CardHead | null;
  /** Сравнение над полями — у шага «Новая позиция»: набранное ↔ лежащее. */
  compare?: { title: string; rows: (card: NomenclatureCard) => CompareRow[] };
  /** Как нарисовать «Выбрать эту позицию»; `null` — кнопки нет (её держит подвал шага). */
  pick?: 'tonal' | 'filled' | null;
  onPick: (id: string, name: string | null) => void;
}) {
  const card = useNomenclatureCard(id);
  const { data: access = NO_ACCESS } = useAccess();

  if (id === null) return (
    <p className="text-xs text-fg4">Наведите на позицию или пройдите стрелками — здесь будет её карточка.</p>
  );

  if (card.isError && isNotFound(card.error)) return (
    <div className="space-y-2">
      {head && <Title name={head.name} />}
      <Refusal>Этой позиции в справочнике больше нет: её удалили, пока окно было открыто. Выберите другую.</Refusal>
    </div>
  );

  if (card.isError) return (
    <div className="space-y-2">
      {head && <Title name={head.name} why={head.why} />}
      <Refusal>
        Карточка не прочитана: {apiError(card.error, 'сервер отказал')}. Позицию можно выбрать и так — по названию.
      </Refusal>
      <Button size="sm" variant="text" onClick={() => void card.refetch()}>Повторить</Button>
    </div>
  );

  if (!card.data) return (
    <div className="space-y-3" aria-busy="true">
      {head ? <Title name={head.name} why={head.why} /> : <div className="h-5 w-3/4 rounded bg-muted animate-pulse" />}
      <div className="space-y-2">
        {[0, 1, 2, 3].map(i => <div key={i} className="h-4 rounded bg-muted animate-pulse" />)}
      </div>
    </div>
  );

  const data = card.data;
  const meta = cardMeta(data);
  const rows = compare?.rows(data) ?? [];
  // Запись не системного уровня на странице «Общие данные» не стоит — ссылки на неё нет.
  const link = data.scope === null ? recordLink(openTo(CATALOG_RECORD, access), data.id) : null;

  return (
    <div className="space-y-3">
      <Title name={data.name} why={head?.why} archived={data.archived} />

      {rows.length > 0 && (
        <section className="space-y-1.5">
          <h4 className="text-caption font-medium uppercase tracking-wide text-fg3">{compare!.title}</h4>
          <dl className="grid grid-cols-[6rem_minmax(0,1fr)] gap-x-3 gap-y-1 text-sm">
            {rows.map(row => (
              <div key={row.key} className="contents">
                <dt className="text-xs text-fg3">{row.label}</dt>
                <dd className={COMPARE_TONE[row.tone]}>{row.text}</dd>
              </div>
            ))}
          </dl>
        </section>
      )}

      {onlyName(data) ? (
        <p className="text-xs text-fg3">Кроме названия, у позиции ничего не заполнено — сверить не с чем.</p>
      ) : (
        <dl className="grid grid-cols-[8rem_minmax(0,1fr)] gap-x-3 gap-y-1 text-sm border-t border-stroke pt-3">
          {data.lines.map(line => {
            const value = lineValue(line);
            return (
              <div key={line.key} className="contents">
                <dt className="text-xs text-fg3 pt-0.5">
                  {line.title}
                  {line.identity && <span className="ml-1 text-caption text-fg4">ключ</span>}
                </dt>
                <dd className={`break-words ${TONE[value.tone]}`}>
                  {value.text}
                  {line.inheritedFrom && (
                    <span className="ml-1 text-caption text-fg4">от основы «{line.inheritedFrom}»</span>
                  )}
                </dd>
              </div>
            );
          })}
          {data.aliases.length > 0 && (
            <>
              <dt className="text-xs text-fg3 pt-0.5">Другие имена</dt>
              <dd className="text-fg1 break-words">{data.aliases.join(', ')}</dd>
            </>
          )}
          <dt className="text-xs text-fg3 pt-0.5">Вид</dt>
          <dd className="text-fg1">
            {data.typeName}
            {data.scope && <span className="ml-1 text-caption text-fg4">уровень: {SCOPE_LABELS[SCOPE[data.scope]].toLowerCase()}</span>}
          </dd>
        </dl>
      )}

      {meta && <p className="text-caption text-fg4">{meta}</p>}
      <p className="text-xs text-fg2 rounded-md bg-muted px-2.5 py-1.5">{chosenText(data.chosen)}</p>

      {/* Архивную в новую строку сервер не примет — кнопки нет, причина названа. */}
      {data.archived ? (
        <p className="text-xs text-fg3">Позиция в архиве: в строку её не выбрать. Вернуть её может тот, кто ведёт справочник.</p>
      ) : (pick || link) && (
        <div className="flex flex-wrap items-center gap-3 pt-1">
          {pick && (
            <Button size="sm" variant={pick} onClick={() => onPick(data.id, data.name)}>Выбрать эту позицию</Button>
          )}
          {/* Новой вкладкой: окно выбора стоит над несохранёнными строками, уводить страницу нельзя. */}
          {link && (
            <a href={link.to} target="_blank" rel="noopener noreferrer"
              className="inline-flex items-center gap-1 text-xs text-brand hover:underline">
              {link.label} <ExternalLink size={12} />
            </a>
          )}
        </div>
      )}
    </div>
  );
}

function Title({ name, why, archived = false }: { name: string | null; why?: string | null; archived?: boolean }) {
  return (
    <div>
      <p className="text-base font-medium leading-snug text-fg1 break-words">
        {name ?? 'без названия'}
        {archived && <ArchivedMark className="ml-2 align-middle" />}
      </p>
      {why && <p className="mt-0.5 text-xs text-fg3">{why}</p>}
    </div>
  );
}

/** Отказ — красной строкой со значком: им же говорит шаг «Новая позиция». */
export function Refusal({ children }: { children: React.ReactNode }) {
  return (
    <p className="flex items-start gap-2 text-xs text-danger">
      <TriangleAlert size={13} className="shrink-0 mt-0.5" />
      <span>{children}</span>
    </p>
  );
}
