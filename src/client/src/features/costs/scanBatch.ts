import { useSyncExternalStore } from 'react';
import { apiError } from '@/shared/utils/apiError';
import { ruCount, ruPlural } from '@/shared/utils/pluralize';

/**
 * Пакет сканов: несколько файлов разом — по черновику на файл (задача D4, issue #1093).
 *
 * <p>Пакет — это N последовательных запросов «счёт из скана», а не один большой: у каждого файла свой
 * исход, и отказ одного не отменяет остальных. Последовательно, а не разом: порядок черновиков в
 * списке повторяет порядок файлов, а остановить можно между файлами, не оборвав ни одного.</p>
 *
 * <p>Состояние живёт ВНЕ страницы: ушёл в реестр и вернулся — полоса на месте. Иначе пакет, который
 * ещё грузится, выглядел бы после возврата как «ничего не происходит», и те же файлы бросили бы
 * второй раз — а черновики пачкой не убрать.</p>
 */

/** За раз. Предел стоит от «бросил не ту папку» (решение владельца 09.10.2026). */
export const MAX_FILES = 50;
/** На файл — тот же предел, что называет сервер. */
export const MAX_BYTES = 50 * 1024 * 1024;

const READABLE = ['application/pdf', 'image/png', 'image/jpeg'];

/** Чем кончился запрос: черновик завёлся — вот его счёт. */
export type SendScan = (file: File) => Promise<string>;

export interface RejectedScan {
  name: string;
  reason: string;
  /** Файл, который имеет смысл отправить ещё раз; `null` — повтор дал бы тот же отказ. */
  retry: File | null;
}

export interface ScanBatch {
  phase: 'running' | 'stopping' | 'done';
  total: number;
  /** Сколько файлов уже получили исход — принятых и непринятых вместе. */
  settled: number;
  created: string[];
  rejected: RejectedScan[];
  /** Пакет остановился сам: отказ, после которого остальные файлы слать бессмысленно. */
  halted: string | null;
  /** Набор не принят целиком, ни один файл не отправлен. */
  refused: string | null;
}

const NOT_SENT = 'не отправлен — загрузка остановлена';

/** Что не так с файлом ещё до отправки. Повтор тут не поможет — причина в самом файле. */
export function precheck(file: File): string | null {
  if (!READABLE.includes(file.type)) return 'не PDF, PNG или JPEG';
  if (file.size === 0) return 'файл пуст';
  if (file.size > MAX_BYTES) return 'больше 50 МБ';
  return null;
}

/**
 * Отказ запроса — словами и с ответом на два вопроса: слать ли этот файл ещё раз и слать ли остальные.
 *
 * <p>⚠️ Ответ не пришёл — это НЕ «не заведён»: запрос мог дойти, и черновик на сервере есть. Сказать
 * «не заведён» значило бы пригласить к повтору, который даст второй черновик с тем же сканом.</p>
 */
export function failure(e: unknown): { reason: string; retry: boolean; halt: string | null } {
  const status = (e as { response?: { status?: number } })?.response?.status;
  if (status === undefined)
    return { reason: 'ответ не пришёл — черновик мог завестись, проверьте список перед повтором', retry: true, halt: null };
  if (status === 413) return { reason: 'больше, чем принимает сервер', retry: false, halt: null };
  const text = apiError(e, `отказ ${status}`);
  // Вход истёк, права нет, частота запросов превышена — следующий файл получит то же самое.
  if (status === 401 || status === 403 || status === 429) return { reason: NOT_SENT, retry: true, halt: text };
  // 400 — про сам файл: повтор принёс бы тот же отказ.
  return { reason: `сервер отказал: ${text}`, retry: status !== 400, halt: null };
}

/** Порядок отправки — по имени, числа внутри имени числами: `скан 2` раньше, чем `скан 10`. */
export const inOrder = (files: File[]): File[] =>
  [...files].sort((a, b) => a.name.localeCompare(b.name, 'ru', { numeric: true }));

const drafts = (n: number) => ruCount(n, 'черновик', 'черновика', 'черновиков');

/** Сколько повторяемых среди непринятых — число на кнопке «Повторить непринятые». */
export const retryable = (batch: ScanBatch): File[] =>
  batch.rejected.flatMap(r => (r.retry ? [r.retry] : []));

/** Заголовок полосы — одной строкой на любое состояние пакета. */
export function batchTitle(batch: ScanBatch): string {
  if (batch.refused) return batch.refused;
  if (batch.phase === 'stopping') return 'Останавливаем — догружается текущий файл…';
  if (batch.phase === 'running') return `Загружаем сканы: ${batch.settled} из ${batch.total}`;
  const made = batch.created.length;
  if (batch.halted) return `Загрузка остановлена: ${batch.halted} Заведено ${made} из ${batch.total}.`;
  if (batch.rejected.length === 0)
    return `${ruPlural(made, 'Заведён', 'Заведено', 'Заведено')} ${drafts(made)}. ` +
      'Распознавание идёт — состояние в строках списка.';
  return `Заведено ${made} из ${batch.total}. Не принято ${batch.rejected.length}:`;
}

/** Набор больше предела — отказ ВСЕМУ набору, а не «первые пятьдесят»: какие из них первые, человек не выбирал. */
export const tooMany = (count: number): string | null => count > MAX_FILES
  ? `Выбрано ${ruCount(count, 'файл', 'файла', 'файлов')} — за раз принимается до ${MAX_FILES}. Ничего не загружено.`
  : null;

// ── Хранилище ────────────────────────────────────────────────────────────────────────────────────

let state: ScanBatch | null = null;
const listeners = new Set<() => void>();

function put(next: ScanBatch | null) {
  state = next;
  listeners.forEach(l => l());
}

const patch = (change: Partial<ScanBatch>) => { if (state) put({ ...state, ...change }); };

/** Пока файлы не отправлены, закрытие вкладки спрашивает: недогруженное пропало бы молча. */
function warnOnLeave(e: BeforeUnloadEvent) { e.preventDefault(); }

async function run(files: File[], send: SendScan) {
  // Через globalThis: логику гоняют и без окна (тесты), а там слушать нечего.
  globalThis.addEventListener?.('beforeunload', warnOnLeave as EventListener);
  try {
    for (const file of files) {
      const now = state!;
      const reject = (reason: string, retry: boolean) =>
        patch({ settled: now.settled + 1, rejected: [...now.rejected, { name: file.name, reason, retry: retry ? file : null }] });

      if (now.phase === 'stopping' || now.halted) { reject(NOT_SENT, true); continue; }
      const problem = precheck(file);
      if (problem) { reject(problem, false); continue; }
      try {
        const id = await send(file);
        // Состояние читается заново: за время запроса могли нажать «Остановить».
        patch({ settled: state!.settled + 1, created: [...state!.created, id] });
      } catch (e) {
        const { reason, retry, halt } = failure(e);
        patch({
          settled: state!.settled + 1, halted: halt,
          rejected: [...state!.rejected, { name: file.name, reason, retry: retry ? file : null }],
        });
      }
    }
  } finally {
    globalThis.removeEventListener?.('beforeunload', warnOnLeave as EventListener);
    patch({ phase: 'done' });
  }
}

const idle = () => state === null || state.phase === 'done';

/**
 * Начать пакет. Пока идёт прежний — не начинается: два пакета разом перемешали бы и порядок, и счёт.
 *
 * @param folders имена брошенных папок: внутрь не заходим, а молча пропустить — значит потерять.
 */
export function startBatch(files: File[], folders: string[], send: SendScan) {
  if (!idle()) return;
  const count = files.length + folders.length;
  const refused = tooMany(count);
  const rejected: RejectedScan[] = refused ? [] : folders.map(name =>
    ({ name, reason: 'это папка — перетащите файлы из неё', retry: null }));
  put({
    phase: refused ? 'done' : 'running', total: refused ? 0 : count, settled: rejected.length,
    created: [], rejected, halted: null, refused,
  });
  if (!refused) void run(inOrder(files), send);
}

/** Текущий запрос не рвётся: оборванный не скажет, завёлся ли черновик. Не уходят только следующие. */
export function stopBatch() {
  if (state?.phase === 'running') patch({ phase: 'stopping' });
}

/** Ещё раз — только те, у кого повтор может кончиться иначе; счёт пакета продолжается, а не начинается заново. */
export function retryRejected(send: SendScan) {
  if (!state || state.phase !== 'done') return;
  const again = retryable(state);
  if (again.length === 0) return;
  put({
    ...state, phase: 'running', halted: null, settled: state.settled - again.length,
    rejected: state.rejected.filter(r => !r.retry),
  });
  void run(again, send);
}

export function dismissBatch() {
  if (idle()) put(null);
}

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
};

export const useScanBatch = (): ScanBatch | null => useSyncExternalStore(subscribe, () => state);

/** Только для тестов: хранилище общее на модуль. */
export const resetBatchForTests = () => put(null);
export const currentBatch = () => state;
