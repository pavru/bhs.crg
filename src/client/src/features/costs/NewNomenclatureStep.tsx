import { useRef, useState } from 'react';
import { ArrowLeft, TriangleAlert } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import {
  useCreateNomenclature, useNomenclatureIntake, useSimilarNomenclature,
  type NomenclaturePosition, type SimilarPositions,
} from '@/shared/api/nomenclatureIntake';
import {
  createBody, defaultKind, missingFields, prefill, prefillHint, similarBody,
  type IntakeDraft, type LineWords,
} from './newNomenclature';

const field = 'w-full rounded border border-stroke bg-surface px-2 py-1 text-sm text-fg outline-none';

/**
 * Шаг «Новая позиция номенклатуры» в окне выбора позиции (задача C3, issue #1079, ТЗ COST-7.1).
 *
 * <p>Второй шаг ТОГО ЖЕ окна, а не третье окно поверх двух: под ним несохранённые строки счёта.</p>
 *
 * <p>⚠️ <b>Кнопки создания нет, пока нет ответа о похожих</b> — ни пока он едет, ни когда он не
 * пришёл. Новая позиция без показа похожих и есть то, от чего это окно заведено: дубль в справочнике,
 * после которого затраты сводят вручную.</p>
 *
 * <p>Что спрашивать, решает сервер по схеме вида: ключей полей здесь нет. Вид, у которого обязательно
 * то, что окну заполнить нечем, назван негодным — с причиной, а не с кнопкой, которая откажет.</p>
 */
export function NewNomenclatureStep({ from, onBack, onPick }: {
  /** Слова строки счёта — подставляются в поля. */
  from: LineWords;
  onBack: () => void;
  onPick: (id: string, name: string | null) => void;
}) {
  const toast = useToast();
  const kinds = useNomenclatureIntake(true);
  const create = useCreateNomenclature();
  const [chosen, setChosen] = useState<string | null>(null);
  const [edited, setEdited] = useState<IntakeDraft | null>(null);
  // Набранное, о котором спрошен сервер: отстаёт от полей на паузу в наборе. `null` — спрашиваем как есть.
  const [asked, setAsked] = useState<Record<string, string> | null>(null);
  const [refused, setRefused] = useState<string | null>(null);
  const timer = useRef<number | null>(null);

  const all = kinds.data ?? [];
  const kind = chosen ? all.find(k => k.typeId === chosen) ?? null : defaultKind(all);
  const fit = kind !== null && kind.refusals.length === 0;
  const draft = edited ?? (kind ? prefill(kind, from) : { values: {}, refs: {} });

  const wanted = fit ? similarBody(kind, draft.values) : null;
  const body = fit ? similarBody(kind, asked ?? draft.values) : null;
  const settled = JSON.stringify(wanted) === JSON.stringify(body);
  const similar = useSimilarNomenclature(body);

  function pickKind(typeId: string) {
    const next = all.find(k => k.typeId === typeId) ?? null;
    setChosen(typeId || null);
    setRefused(null);
    setAsked(null);
    // Набранное переезжает в поля нового вида с теми же ключами: сменить вид — не начать заново.
    if (next && edited) {
      const base = prefill(next, from);
      const keys = new Set(next.fields.filter(f => f.options === null).map(f => f.key));
      setEdited({
        values: { ...base.values, ...Object.fromEntries(Object.entries(edited.values).filter(([k]) => keys.has(k))) },
        refs: base.refs,
      });
    } else setEdited(null);
  }

  function type(key: string, value: string) {
    const next = { ...draft, values: { ...draft.values, [key]: value } };
    setEdited(next);
    setRefused(null);
    // Задержка в обработчике, а не в эффекте — как у поиска в этом же окне.
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setAsked(next.values), 250);
  }

  async function submit() {
    if (!kind) return;
    setRefused(null);
    try {
      const made = await create.mutateAsync(createBody(kind, draft));
      toast.success(`Позиция заведена: ${made.name ?? 'без названия'}. Остальные поля дополните в справочнике.`);
      onPick(made.id, made.name);
    } catch (e) {
      // Отказ — в этом же шаге, а не тостом: набранное цело, и причина стоит рядом с ним.
      setRefused(apiError(e, 'сервер отказал'));
    }
  }

  const missing = kind && fit ? missingFields(kind, draft) : [];

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 border-b border-stroke pb-2">
        <button type="button" onClick={onBack} className="flex items-center gap-1 text-xs text-fg3 hover:text-fg">
          <ArrowLeft size={13} /> К поиску
        </button>
      </div>

      {kinds.isPending && <p className="text-xs text-fg4">Читаем, что спросить о позиции…</p>}
      {kinds.isError && (
        <Refusal>Новую позицию сейчас не завести: {apiError(kinds.error, 'сервер отказал')}</Refusal>
      )}

      {kinds.data && (
        <label className="block space-y-1">
          <span className="text-xs text-fg3">Вид</span>
          <select value={kind?.typeId ?? ''} onChange={e => pickKind(e.target.value)} className={field}>
            <option value="">— выберите вид позиции —</option>
            {all.map(k => <option key={k.typeId} value={k.typeId}>{k.name}</option>)}
          </select>
        </label>
      )}

      {kind && !fit && (
        <Refusal>
          Позицию вида «{kind.name}» отсюда не завести: {kind.refusals.join('; ')}. Заведите её в
          справочнике номенклатуры и найдите здесь поиском. Строку можно сохранить без позиции.
        </Refusal>
      )}

      {kind && fit && (
        <>
          {kind.fields.map(f => (
            <label key={f.key} className="block space-y-1">
              <span className="text-xs text-fg3">{f.title}{f.required ? ' *' : ''}</span>
              {f.options === null ? (
                <input value={draft.values[f.key] ?? ''} onChange={e => type(f.key, e.target.value)} className={field} />
              ) : (
                <select value={draft.refs[f.key] ?? ''} className={field}
                  onChange={e => setEdited({ ...draft, refs: { ...draft.refs, [f.key]: e.target.value } })}>
                  <option value="">— не выбрано —</option>
                  {f.options.map(o => <option key={o.id} value={o.id}>{o.name ?? 'без названия'}</option>)}
                </select>
              )}
              {prefillHint(kind, f, from) && draft.values[f.key] === prefill(kind, from).values[f.key] && (
                <span className="block text-[11px] text-fg4">{prefillHint(kind, f, from)}</span>
              )}
            </label>
          ))}
          <p className="text-[11px] text-fg4">
            В справочник попадёт только названное здесь. Остальные поля вида «{kind.name}» дополните в справочнике.
          </p>

          <Similar body={body !== null} settled={settled} similar={similar} onPick={onPick}
            create={(
              <Button size="sm" disabled={missing.length > 0 || create.isPending} onClick={() => void submit()}
                variant={similar.data?.similar.length ? 'outlined' : 'filled'}
                title={missing.length > 0 ? `Не заполнено: ${missing.join(', ')}` : undefined}>
                {similar.data?.similar.length ? 'Завести новую — похожие не подходят' : 'Завести позицию'}
              </Button>
            )} />
          {missing.length > 0 && body !== null && (
            <p className="text-[11px] text-fg4">Не заполнено: {missing.join(', ')}.</p>
          )}
          {refused && <Refusal>{refused}</Refusal>}
        </>
      )}
    </div>
  );
}

function Refusal({ children }: { children: React.ReactNode }) {
  return (
    <p className="flex items-start gap-2 text-xs text-danger">
      <TriangleAlert size={13} className="shrink-0 mt-0.5" />
      <span>{children}</span>
    </p>
  );
}

/** Блок «Похожие в справочнике»: что на экране и есть ли кнопка создания — по ответу сервера. */
function Similar({ body, settled, similar, create, onPick }: {
  body: boolean;
  settled: boolean;
  similar: { data?: SimilarPositions; isFetching: boolean; isError: boolean; error: unknown; refetch: () => unknown };
  create: React.ReactNode;
  onPick: (id: string, name: string | null) => void;
}) {
  const head = <p className="text-xs font-medium text-fg2">Похожие в справочнике</p>;

  if (!body) return <div className="space-y-1">{head}
    <p className="text-xs text-fg4">Назовите позицию — проверим, нет ли такой в справочнике.</p></div>;
  if (!settled || similar.isFetching) return <div className="space-y-1">{head}
    <p className="text-xs text-fg4">Ищем похожие…</p></div>;
  if (similar.isError || !similar.data) return (
    <div className="space-y-1">{head}
      <Refusal>
        Похожие не проверены: {apiError(similar.error, 'сервер отказал')}. Без проверки позиция не
        заводится — иначе в справочнике появится дубль.{' '}
        <button type="button" className="underline" onClick={() => void similar.refetch()}>Повторить</button>
      </Refusal>
    </div>
  );

  const { exact, similar: hits, more, unreadable } = similar.data;
  const unread = unreadable > 0 && (
    <p className="text-[11px] text-warning">
      Не сверено позиций: {unreadable} — их записи прочитать не удалось. Это не «не похожи».
    </p>
  );

  if (exact) return (
    <div className="space-y-1">{head}
      {exact.archived ? (
        <p className="text-xs text-fg2">
          В архиве есть такая позиция: «{exact.name ?? 'без названия'}». Заводить заново не нужно —
          вернуть из архива может тот, кто ведёт справочник.
        </p>
      ) : (
        <>
          <p className="text-xs text-fg2">Такая позиция уже есть:</p>
          <Row position={exact} action="Выбрать" onPick={onPick} />
        </>
      )}
      <p className="text-xs text-fg4">Вторую такую же завести нельзя.</p>
    </div>
  );

  return (
    <div className="space-y-1">{head}
      {hits.map(hit => (
        <Row key={hit.position.id} position={hit.position} why={hit.why} action="Это она" onPick={onPick} />
      ))}
      {more && <p className="text-xs text-warning">Есть ещё похожие — уточните наименование.</p>}
      {hits.length === 0 && <p className="text-xs text-fg3">Похожих в справочнике не найдено.</p>}
      {/* Оговорка — всегда: «не найдено» значит «правила не нашли», а не «дубля нет». */}
      <p className="text-[11px] text-fg4">
        Сверка — по совпадению слов и значений: написанное иначе («3х2,5» и «3*2.5») она не найдёт.
      </p>
      {unread}
      <div className="flex justify-end pt-1">{create}</div>
    </div>
  );
}

function Row({ position, why, action, onPick }: {
  position: NomenclaturePosition;
  why?: string;
  action: string;
  onPick: (id: string, name: string | null) => void;
}) {
  const label = (
    <>
      <span className="text-sm text-fg truncate">{position.name ?? 'без названия'}</span>
      {why && <span className="text-[11px] text-fg3 truncate">{why}</span>}
      <span className="text-[11px] text-fg4 shrink-0">{position.type}</span>
    </>
  );
  // Архивную выбрать нельзя — сервер её в новую строку не примет; но показать обязаны: иначе её
  // заведут заново.
  if (position.archived) return (
    <div className="px-3 py-1.5 flex items-baseline gap-2">
      {label}<span className="ml-auto text-[11px] text-fg4 shrink-0">в архиве</span>
    </div>
  );
  return (
    <button type="button" onClick={() => onPick(position.id, position.name)}
      className="w-full text-left px-3 py-1.5 rounded hover:bg-surface2 flex items-baseline gap-2">
      {label}<span className="ml-auto text-xs text-brand shrink-0">{action}</span>
    </button>
  );
}
