import { useState } from 'react';
import { useFreshInvoice, type InvoiceView } from '@/shared/api/invoices';

/**
 * На каком виде счёта стоит черновик формы — и какую версию он вправе назвать при сохранении
 * (issue #1176).
 *
 * <p>Сервер отказывает правке, собранной по устаревшему виду: правка называет версию счёта, которую
 * видел человек. Версия у счёта ОДНА, а форм на экране несколько — шапка, строки, разноска, — и вид
 * счёта под ними обновляется сам (после чужой и после своей правки соседней части). Назвать «версию,
 * какая сейчас в кэше» значило бы соврать: черновик строк собран по прежним строкам, а подпись под ним
 * стояла бы свежая — и чужая правка затёрлась бы ровно так же, как до этой задачи.</p>
 *
 * <p>Поэтому черновик помнит, на чём собран: версию и <b>подпись своей части</b> — то из вида, из
 * чего он построен. Вид сменился, а подпись та же (сосед поправил шапку, я правлю строки) — основа
 * молча переезжает на новую версию: мой черновик по-прежнему собран по тому, что лежит в базе.
 * Подпись другая, а правок у меня нет — черновик пересобирается из свежего вида. Подпись другая и
 * правки есть — это и есть «счёт изменили, пока вы правили»: называется прежняя версия, сервер
 * отказывает, набранное остаётся на экране.</p>
 *
 * <p>⚠️ <b>В подпись идёт только то, что меняется ВМЕСТЕ с версией счёта.</b> Название позиции
 * номенклатуры приезжает в том же виде, а меняется в справочнике, без счёта: попади оно в подпись,
 * основа запомнила бы прежнее название, и следующая чужая правка шапки выглядела бы правкой строк.</p>
 */
export interface DraftBase<S = string> {
  version: string;
  signature: S;
}

export type BaseStep<S = string> =
  /** Вид тот же или переехал без последствий для черновика. */
  | { kind: 'same'; base: DraftBase<S> }
  /** Часть изменилась, правок нет: черновик надо пересобрать из свежего вида. */
  | { kind: 'rebuild'; base: DraftBase<S> }
  /** Часть изменилась под несохранёнными правками: основа остаётся прежней. */
  | { kind: 'stale'; base: DraftBase<S> };

/**
 * Что делать с основой черновика, когда пришёл вид счёта. Чистая функция — её и проверяют тесты.
 *
 * @param same равны ли две подписи. Своё сравнение нужно шапке: её подпись — реквизиты целиком, а
 *   «моя часть» — только поля, которые человек правит (см. `InvoiceForm`).
 */
export function stepBase<S = string>(
  base: DraftBase<S>, version: string, signature: S, dirty: boolean,
  same: (a: S, b: S) => boolean = Object.is,
): BaseStep<S> {
  if (base.version === version) return { kind: 'same', base };
  if (same(base.signature, signature)) return { kind: 'same', base: { version, signature } };
  return dirty ? { kind: 'stale', base } : { kind: 'rebuild', base: { version, signature } };
}

const isConflict = (error: unknown) => (error as { response?: { status?: number } })?.response?.status === 409;

/**
 * @param view вид счёта, пришедший в форму.
 * @param signatureOf подпись части, из которой построен черновик (строки, части разноски, реквизиты).
 * @param dirty есть ли несохранённые правки.
 * @param options `same` — своё сравнение подписей; `initial` — основа, с которой черновик ПРИШЁЛ, если
 *   он собран не по виду на момент монтирования (предпросмотр, посчитанный до открытия матрицы).
 * @returns `stale` — часть изменили под правками; `rebuild` — в этом рендере черновик надо пересобрать
 *   из вида; `rebase` — принять вид за основу (по кнопке «Перечитать счёт»); `save` — записать черновик.
 */
export function useDraftBase<S = string>(
  view: InvoiceView, signatureOf: (of: InvoiceView) => S, dirty: boolean,
  options: { same?: (a: S, b: S) => boolean; initial?: DraftBase<S> } = {},
) {
  const { version } = view;
  const signature = signatureOf(view);
  const [base, setBase] = useState<DraftBase<S>>(() => options.initial ?? { version, signature });
  const step = stepBase(base, version, signature, dirty, options.same);
  const readFresh = useFreshInvoice();

  // Состояние правится прямо в рендере — приём React для «состояния, выведенного из пропсов»: эффект
  // отработал бы после кадра, и кадр со свежим видом успел бы нарисоваться поверх прежней основы.
  if (step.base !== base) setBase(step.base);

  /**
   * Записать черновик, назвав версию его основы.
   *
   * <p>Сначала — как есть: в обычном случае вид на экране свеж, и запись проходит одним запросом.
   * Отказ 409 означает одно из двух, и различить их может только свежепрочитанный счёт. Изменили
   * ДРУГУЮ часть (сосед сохранил шапку, а кэш ещё прежний) — моя подпись та же, и запись повторяется со
   * свежей версией: человек отказа не видит. Изменили МОЮ — отказ остаётся отказом.</p>
   *
   * <p>⚠️ 409 бывает и не про версию (счёт заперт, разошлась отметка разноски). Тогда свежая версия
   * равна названной, и повтор был бы той же правкой второй раз — отказ уходит наружу как есть.</p>
   *
   * @param send получает версию и, на повторе, свежий вид — шапка кладёт правки поверх него.
   */
  async function save<T>(send: (seen: string, fresh: InvoiceView | null) => Promise<T>): Promise<T> {
    try {
      return await send(step.base.version, null);
    } catch (refusal) {
      if (!isConflict(refusal)) throw refusal;

      const fresh = await readFresh(view.id);
      const seen = stepBase(step.base, fresh.version, signatureOf(fresh), true, options.same).base.version;
      if (seen !== fresh.version || seen === step.base.version) throw refusal;
      return await send(seen, fresh);
    }
  }

  return {
    save,
    stale: step.kind === 'stale',
    rebuild: step.kind === 'rebuild',
    rebase: () => setBase({ version, signature }),
  };
}
