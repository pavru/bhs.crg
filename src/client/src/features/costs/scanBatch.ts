import { useSyncExternalStore } from 'react';
import { onTokenChanged } from '@/shared/api/token';
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
 * второй раз — а черновики пачкой не убрать. По той же причине итог переживает и перезагрузку
 * страницы (его копия лежит в хранилище вкладки): истёкший вход уводит на страницу входа именно
 * перезагрузкой, и без копии человек не узнал бы, какие файлы ушли.</p>
 *
 * <p>У пакета есть владелец. Полосу видит только он: выход из учётной записи страницу не
 * перезагружает, и следующий вошедший в той же вкладке иначе увидел бы чужие имена файлов и кнопку
 * «Повторить», которая отправила бы чужие файлы от его имени (ревью PR #1260).</p>
 */

/** За раз. Предел стоит от «бросил не ту папку» (решение владельца 09.10.2026). */
export const MAX_FILES = 50;
/** На файл — тот же предел, что называет сервер (сверяет `ScanLimitsAgreeTests`). */
export const MAX_BYTES = 50 * 1024 * 1024;
/** Что распознаётся. Тот же перечень у сервера; из него же собран `accept` у выбора файлов. */
export const READABLE = ['application/pdf', 'image/png', 'image/jpeg'];
export const SCAN_ACCEPT = READABLE.join(',');
/** Так браузер называет файл, вида которого не знает. */
const UNNAMED = 'application/octet-stream';

export interface BatchPort {
  /** Чей пакет — идентификатор вошедшего. */
  owner: string;
  /** Отправить файл; ответ — счёт заведённого черновика. Списки при этом НЕ перечитываются. */
  send: (file: File) => Promise<string>;
  /** Перечитать список и числа отборов. Зовётся раз в несколько файлов и в конце, а не на каждый. */
  refresh: () => void;
}

export interface RejectedScan {
  name: string;
  reason: string;
  /** Файл, который имеет смысл отправить ещё раз; `null` — повтор дал бы тот же отказ или файла уже нет. */
  retry: File | null;
}

export interface ScanBatch {
  owner: string;
  phase: 'running' | 'stopping' | 'done';
  total: number;
  /** Сколько файлов уже получили исход — принятых и непринятых вместе. */
  settled: number;
  created: string[];
  rejected: RejectedScan[];
  /** Имена файлов без исхода, по порядку; первый — тот, что отправляется сейчас. */
  pending: string[];
  /** Пакет остановился сам: отказ, после которого остальные файлы слать бессмысленно. */
  halted: string | null;
  /** Набор не принят целиком, ни один файл не отправлен. */
  refused: string | null;
}

const NOT_SENT = 'не отправлен — загрузка остановлена';
const MAYBE = 'черновик мог завестись, проверьте список перед повтором';
/** Списки перечитываются раз в столько заведённых черновиков — и ещё раз в конце. */
const REFRESH_EVERY = 5;

/** Что не так с файлом ещё до отправки. Повтор тут не поможет — причина в самом файле. */
export function precheck(file: File): string | null {
  // Вид файла определяет сервер, по содержимому (issue #1265). Здесь отсекается только то, что
  // браузер сам НАЗВАЛ другим видом: гнать на сервер пятьдесят мегабайт ради известного отказа
  // незачем. Файл без названного вида (так приходит PDF без расширения) идёт на сервер — решит он.
  if (file.type && file.type !== UNNAMED && !READABLE.includes(file.type)) return 'не PDF, PNG или JPEG';
  if (file.size === 0) return 'файл пуст';
  if (file.size > MAX_BYTES) return 'больше 50 МБ';
  return null;
}

/**
 * Отказ запроса — словами и с ответом на два вопроса: слать ли этот файл ещё раз и слать ли остальные.
 *
 * <p>⚠️ Ответ не пришёл — это НЕ «не заведён»: запрос мог дойти, и черновик на сервере есть. Сказать
 * «не заведён» значило бы пригласить к повтору, который даст второй черновик с тем же сканом.</p>
 *
 * <p>Про сам файл говорят только 400 и 413 — после них пакет идёт дальше. Всё остальное одинаково
 * для любого файла: нет связи, сервер лёг, права нет, частота превышена. Слать дальше значило бы
 * получить тот же отказ на каждый файл — и сорок семь строк «проверьте список» там, где запросы
 * заведомо не дошли (ревью PR #1260).</p>
 */
export function failure(e: unknown): { reason: string; retry: boolean; halt: string | null } {
  const status = (e as { response?: { status?: number } })?.response?.status;
  if (status === undefined)
    return { reason: `ответ не пришёл — ${MAYBE}`, retry: true, halt: 'сервер не отвечает' };
  if (status === 413) return { reason: 'больше, чем принимает сервер', retry: false, halt: null };
  const text = apiError(e, `отказ ${status}`);
  if (status === 400) return { reason: `сервер отказал: ${text}`, retry: false, halt: null };
  return { reason: `сервер отказал: ${text}`, retry: true, halt: text };
}

/** Порядок отправки — по имени, числа внутри имени числами: `скан 2` раньше, чем `скан 10`. */
export const inOrder = (files: File[]): File[] =>
  [...files].sort((a, b) => a.name.localeCompare(b.name, 'ru', { numeric: true }));

const drafts = (n: number) => ruCount(n, 'черновик', 'черновика', 'черновиков');

/** Чужой текст — законченной фразой: за ним в заголовке идёт своя. */
const sentence = (text: string) => (/[.!?…]$/.test(text.trim()) ? text.trim() : `${text.trim()}.`);

/** Сколько повторяемых среди непринятых — число на кнопке «Повторить непринятые». */
export const retryable = (batch: ScanBatch): File[] =>
  batch.rejected.flatMap(r => (r.retry ? [r.retry] : []));

/** Заголовок полосы — одной строкой на любое состояние пакета. */
export function batchTitle(batch: ScanBatch): string {
  if (batch.refused) return batch.refused;
  if (batch.phase === 'stopping') return 'Останавливаем — догружается текущий файл…';
  if (batch.phase === 'running') return `Загружаем сканы: ${batch.settled} из ${batch.total}`;
  const made = batch.created.length;
  if (batch.halted) return `Загрузка остановлена: ${sentence(batch.halted)} Заведено ${made} из ${batch.total}.`;
  if (batch.rejected.length === 0)
    return `${ruPlural(made, 'Заведён', 'Заведено', 'Заведено')} ${drafts(made)}. ` +
      'Распознавание идёт — состояние в строках списка.';
  return `Заведено ${made} из ${batch.total}. Не принято ${batch.rejected.length}:`;
}

/** Набор больше предела — отказ ВСЕМУ набору, а не «первые пятьдесят»: какие из них первые, человек не выбирал. */
export const tooMany = (count: number): string | null => count > MAX_FILES
  ? `Выбрано ${ruCount(count, 'файл', 'файла', 'файлов')} — за раз принимается до ${MAX_FILES}. Ничего не загружено.`
  : null;

/**
 * Пакет оборван извне — вход завершился или страницу перезагрузили. Файлов больше нет, остались имена:
 * тот, что отправлялся, мог дойти, остальные не уходили. Повторить нечем — файлы выбирают заново.
 */
export function interrupted(batch: ScanBatch, why: string): ScanBatch {
  if (batch.phase === 'done') return batch;
  const lost: RejectedScan[] = batch.pending.map((name, i) => ({
    name, retry: null,
    reason: i === 0
      ? `ответ не получен — ${why}; черновик мог завестись, проверьте список`
      : `не отправлен — ${why}; выберите файл заново`,
  }));
  return {
    ...batch, phase: 'done', halted: why, pending: [],
    settled: batch.total, rejected: [...batch.rejected, ...lost],
  };
}

// ── Хранилище ────────────────────────────────────────────────────────────────────────────────────

const STORED = 'costs.scanBatch';
const tab = (): Storage | null => {
  try { return typeof sessionStorage === 'undefined' ? null : sessionStorage; } catch { return null; }
};

/** Копия во вкладке: без файлов (их не сохранить), только то, что нужно показать после перезагрузки. */
function restore(): ScanBatch | null {
  try {
    const text = tab()?.getItem(STORED);
    if (!text) return null;
    const stored = JSON.parse(text) as ScanBatch;
    return interrupted({ ...stored, rejected: stored.rejected.map(r => ({ ...r, retry: null })) }, 'страница перезагружена');
  } catch { return null; }
}

let state: ScanBatch | null = restore();
/** Номер запуска: цикл отправки, начатый до обрыва, после него в состояние не пишет. */
let generation = 0;
const listeners = new Set<() => void>();

function put(next: ScanBatch | null) {
  state = next;
  try {
    if (next) tab()?.setItem(STORED, JSON.stringify({ ...next, rejected: next.rejected.map(r => ({ ...r, retry: null })) }));
    else tab()?.removeItem(STORED);
  } catch { /* хранилище недоступно — полоса просто не переживёт перезагрузку */ }
  listeners.forEach(l => l());
}

const patch = (change: Partial<ScanBatch>) => { if (state) put({ ...state, ...change }); };

/** Пока файлы не отправлены, закрытие вкладки спрашивает: недогруженное пропало бы молча. */
function warnOnLeave(e: BeforeUnloadEvent) { e.preventDefault(); }

async function run(files: File[], port: BatchPort) {
  const mine = ++generation;
  const alive = () => mine === generation && state !== null;
  // Через globalThis: логику гоняют и без окна (тесты), а там слушать нечего.
  globalThis.addEventListener?.('beforeunload', warnOnLeave as EventListener);
  try {
    for (const file of files) {
      if (!alive()) return;
      const now = state!;
      const settle = (change: Partial<ScanBatch>) =>
        patch({ settled: state!.settled + 1, pending: state!.pending.slice(1), ...change });
      const reject = (reason: string, retry: boolean) =>
        settle({ rejected: [...state!.rejected, { name: file.name, reason, retry: retry ? file : null }] });

      if (now.phase === 'stopping' || now.halted) { reject(NOT_SENT, true); continue; }
      const problem = precheck(file);
      if (problem) { reject(problem, false); continue; }
      try {
        const id = await port.send(file);
        // За время запроса пакет могли оборвать (вход завершился) — тогда исход уже записан обрывом.
        if (!alive()) return;
        settle({ created: [...state!.created, id] });
        if (state!.created.length % REFRESH_EVERY === 0) port.refresh();
      } catch (e) {
        if (!alive()) return;
        const { reason, retry, halt } = failure(e);
        reject(reason, retry);
        if (halt) patch({ halted: halt });
      }
    }
  } finally {
    globalThis.removeEventListener?.('beforeunload', warnOnLeave as EventListener);
    if (alive()) patch({ phase: 'done' });
    port.refresh();
  }
}

const idle = () => state === null || state.phase === 'done';

/**
 * Начать пакет. Пока идёт прежний — не начинается: два пакета разом перемешали бы и порядок, и счёт.
 *
 * @param folders имена брошенных папок: внутрь не заходим, а молча пропустить — значит потерять.
 */
export function startBatch(files: File[], folders: string[], port: BatchPort) {
  if (!idle()) return;
  const count = files.length + folders.length;
  const refused = tooMany(count);
  const ordered = refused ? [] : inOrder(files);
  const rejected: RejectedScan[] = refused ? [] : folders.map(name =>
    ({ name, reason: 'это папка — перетащите файлы из неё', retry: null }));
  put({
    owner: port.owner, phase: refused ? 'done' : 'running', total: refused ? 0 : count,
    settled: rejected.length, created: [], rejected, pending: ordered.map(f => f.name), halted: null, refused,
  });
  if (!refused) void run(ordered, port);
}

/** Текущий запрос не рвётся: оборванный не скажет, завёлся ли черновик. Не уходят только следующие. */
export function stopBatch() {
  if (state?.phase === 'running') patch({ phase: 'stopping' });
}

/** Ещё раз — только те, у кого повтор может кончиться иначе; счёт пакета продолжается, а не начинается заново. */
export function retryRejected(port: BatchPort) {
  if (!state || state.phase !== 'done' || state.owner !== port.owner) return;
  const again = retryable(state);
  if (again.length === 0) return;
  put({
    ...state, phase: 'running', halted: null, settled: state.settled - again.length,
    rejected: state.rejected.filter(r => !r.retry), pending: again.map(f => f.name),
  });
  void run(again, port);
}

export function dismissBatch() {
  if (idle()) put(null);
}

/**
 * Вход завершился — кнопкой «Выйти» или отказом обновить токен. Идущий пакет обрывается здесь же:
 * слать файлы без входа некому, а при истёкшем входе следом идёт перезагрузка на страницу входа.
 */
export function endSession() {
  if (!state || state.phase === 'done') return;
  generation++;
  put(interrupted(state, 'вход в систему завершился'));
}

onTokenChanged(token => { if (!token) endSession(); });

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
};

/** Пакет вошедшего. Чужой — того, кто работал в этой вкладке раньше, — не показывается. */
export function useScanBatch(owner: string | undefined): ScanBatch | null {
  const batch = useSyncExternalStore(subscribe, () => state);
  return batch !== null && batch.owner === owner ? batch : null;
}

/** Только для тестов: хранилище общее на модуль. */
export const resetBatchForTests = () => { generation++; put(null); };
export const currentBatch = () => state;
