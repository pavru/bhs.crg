import { useSyncExternalStore } from 'react';
import type { InvoiceSameFile } from '@/shared/api/invoices';
import { onTokenChanged } from '@/shared/api/token';
import { apiError } from '@/shared/utils/apiError';
import { ruCount, ruPlural } from '@/shared/utils/pluralize';
import { sameFileLabel } from './invoiceFields';
import { checkDrop, lostDraft, type CheckPort, type HeldFile } from './scanIntake';
import { inOrder } from './scanPrecheck';

export { inOrder, precheck } from './scanPrecheck';

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

/**
 * Куда ходит пакет. Реестр видов, отпечаток и вопрос о повторе — от проверки броска (`scanIntake`, D3 у 14):
 * пакет сначала проверяет все файлы, потом отправляет.
 */
export interface BatchPort extends CheckPort {
  /** Чей пакет — идентификатор вошедшего. */
  owner: string;
  /**
   * Отправить файл; ответ — счёт заведённого черновика. Списки при этом НЕ перечитываются. `acceptRepeat` —
   * сознательный повтор («Завести всё равно»): человек видел, что файл уже приложен.
   */
  send: (file: File, options?: { acceptRepeat?: boolean }) => Promise<{ id: string; sameFile: InvoiceSameFile[] }>;
  /** Перечитать список и числа отборов. Зовётся раз в несколько файлов и в конце, а не на каждый. */
  refresh: () => void;
}

/**
 * Файл, содержимое которого уже приложено к другому счёту (issue #1295). Это НЕ отказ: черновик
 * заведён, и среди непринятых такому файлу не место.
 */
export interface RepeatedScan {
  name: string;
  /** Новый черновик. */
  id: string;
  /**
   * Счета с тем же файлом, от старшего; назван первый. Все, а не один (ревью PR #1305): удалили
   * старшего — у черновика остаются другие двойники, и строка о повторе обязана остаться.
   */
  others: RepeatTwin[];
}

export interface RepeatTwin {
  id: string;
  /** Подпись снята в момент загрузки. */
  label: string;
  fileName: string | null;
  /** Заведён этой же загрузкой: тогда его называют именем файла — оно здесь различает. */
  inBatch: boolean;
}

export interface RejectedScan {
  name: string;
  reason: string;
  /** Файл, который имеет смысл отправить ещё раз; `null` — повтор дал бы тот же отказ или файла уже нет. */
  retry: File | null;
  /**
   * Ответ не пришёл, а отпечаток известен: черновик мог завестись. «Повторить» сначала спрашивает по
   * отпечатку (design D3 у 14) — новый свой двойник, которого нет среди `known`, и есть этот черновик.
   */
  lost?: { print: string; known: string[] };
}

/**
 * Файл, содержимое которого уже приложено к счетам (design D5 у 14): в пакете он не заводится, а ждёт
 * решения — «Открыть» или «Завести всё равно». Файл живёт в памяти вкладки: после перезагрузки его нет, и
 * строка просит выбрать файл заново.
 */
export interface HeldScan {
  name: string;
  file: File | null;
  print: string;
  twins: InvoiceSameFile[];
}

export interface ScanBatch {
  owner: string;
  /** `checking` — отпечатки и вопрос о повторе: ничего ещё не отправлено, остановить нечего. */
  phase: 'checking' | 'running' | 'stopping' | 'done';
  total: number;
  /** Сколько файлов уже получили исход — принятых и непринятых вместе. */
  settled: number;
  created: string[];
  rejected: RejectedScan[];
  /** Заведённые из файла, который уже был приложен к другому счёту. */
  repeats: RepeatedScan[];
  /** Имена файлов без исхода, по порядку; первый — тот, что отправляется сейчас. */
  pending: string[];
  /** Пакет остановился сам: отказ, после которого остальные файлы слать бессмысленно. */
  halted: string | null;
  /** Набор не принят целиком, ни один файл не отправлен. */
  refused: string | null;
  /** Придержанные: файл уже приложен к счетам (лист «Ф», пакет без окна). */
  held: HeldScan[];
  /** Выбранные дважды — то же содержимое под другим именем в этом броске; заведён один черновик. */
  doubled: string[];
  /** Вопрос о повторе не ответил или отпечаток не посчитался: повтор не проверен. */
  unchecked: boolean;
  /** Ответ на заведение потерялся, а черновик нашёлся по отпечатку — второго не заводили. */
  recovered: string[];
}

/** Пакет без придержанных и прочего нового — так его создают тесты и прежние копии из хранилища. */
const EMPTY_EXTRAS = { held: [], doubled: [], unchecked: false, recovered: [] } satisfies Partial<ScanBatch>;

const NOT_SENT = 'не отправлен — загрузка остановлена';
const MAYBE = 'черновик мог завестись, проверьте список перед повтором';
/** Списки перечитываются раз в столько заведённых черновиков — и ещё раз в конце. */
const REFRESH_EVERY = 5;

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


const drafts = (n: number) => ruCount(n, 'черновик', 'черновика', 'черновиков');

/** Чужой текст — законченной фразой: за ним в заголовке идёт своя. */
const sentence = (text: string) => (/[.!?…]$/.test(text.trim()) ? text.trim() : `${text.trim()}.`);

/** Сколько повторяемых среди непринятых — число на кнопке «Повторить непринятые». */
export const retryable = (batch: ScanBatch): File[] =>
  batch.rejected.flatMap(r => (r.retry ? [r.retry] : []));

/** Заголовок полосы — одной строкой на любое состояние пакета. */
export function batchTitle(batch: ScanBatch): string {
  if (batch.refused) return batch.refused;
  if (batch.phase === 'checking') return 'Проверяем файлы — нет ли их уже у счетов…';
  if (batch.phase === 'stopping') return 'Останавливаем — догружается текущий файл…';
  if (batch.phase === 'running') return `Загружаем файлы: ${batch.settled} из ${batch.total}`;
  const made = batch.created.length;
  if (batch.total === 0 && batch.held.length > 0)
    return batch.held.length === 1 ? 'Файл не заведён: он уже приложен к счёту.' : 'Файлы не заведены: они уже приложены к счетам.';
  if (batch.halted) return `Загрузка остановлена: ${sentence(batch.halted)} Заведено ${made} из ${batch.total}.`;
  if (batch.rejected.length === 0)
    return `${ruPlural(made, 'Заведён', 'Заведено', 'Заведено')} ${drafts(made)}. ` +
      'Распознавание идёт — состояние в строках списка.';
  return `Заведено ${made} из ${batch.total}. Не принято ${batch.rejected.length}:`;
}

/** Подпись хода пакета в кнопке активности (D5 у 14): «Счета из файлов · 4 из 7». */
export function batchDoing(batch: ScanBatch): string {
  if (batch.phase === 'checking') return 'Счета из файлов: проверяем';
  return `Счета из файлов · ${batch.settled} из ${batch.total}`;
}

/**
 * Итог одной строкой (лист «Ф»): «Заведено 7 · придержано 2 (тот же файл) · 1 выбран дважды». `null` — у
 * пакета нет ни придержанных, ни выбранных дважды: заголовка тогда хватает.
 */
export function batchTally(batch: ScanBatch): string | null {
  if (batch.held.length === 0 && batch.doubled.length === 0) return null;
  const made = batch.created.length;
  const parts = [batch.phase === 'done' ? `Заведено ${made}` : `Заводим ${batch.settled} из ${batch.total}`];
  if (batch.held.length > 0) parts.push(`придержано ${batch.held.length} (тот же файл)`);
  if (batch.doubled.length > 0) parts.push(`${batch.doubled.length} выбран${batch.doubled.length > 1 ? 'о' : ''} дважды`);
  return parts.join(' · ');
}

/**
 * Счета списка, заведённые этой загрузкой (issue #1294), — в порядке списка, а не файлов: правило
 * сортировки на странице одно. Список отсортирован по дате счёта, и после распознавания черновики
 * разъезжаются между старыми счетами — найти их иначе нечем.
 *
 * <p>`null` — список не пришёл: «не знаем» не выдаётся за «ни одного». Счёт, которого в списке уже
 * нет (удалили), сюда не попадает — поэтому число берут отсюда, а не из `created`.</p>
 */
export function uploadedOf<T extends { id: string }>(items: T[] | undefined, batch: ScanBatch | null): T[] | null {
  if (!items || !batch) return null;
  const created = new Set(batch.created);
  return items.filter(i => created.has(i.id));
}

/**
 * Счёт удалён в этой вкладке — из заведённых он уходит: иначе полоса звала бы «Показать только их»
 * туда, где уже пусто. Удалённый соседом так не узнать — его снимет перечитанный список.
 */
export function forgetCreated(id: string) {
  if (!state) return;
  const touched = state.repeats.some(r => r.id === id || r.others.some(o => o.id === id));
  if (state.created.includes(id) || touched)
    patch({ created: state.created.filter(c => c !== id), repeats: without(state.repeats, o => o !== id) });
}

/**
 * Повторы без ушедших счетов: нет нового черновика — нет строки; ушёл прежний — называется следующий
 * двойник, а строка уходит, только когда не осталось ни одного.
 */
const without = (repeats: RepeatedScan[], kept: (id: string) => boolean): RepeatedScan[] => repeats
  .filter(r => kept(r.id))
  .map(r => ({ ...r, others: r.others.filter(o => kept(o.id)) }))
  .filter(r => r.others.length > 0);

/**
 * Счета, которые есть в списке, — чтобы полоса не звала к удалённому соседом или в другой вкладке
 * (ревью PR #1305). `null` — не знаем: список не пришёл, стоит под отбором сервера или загрузка ещё
 * идёт (он перечитывается не на каждый файл). «Не знаем» повторы не прячет.
 */
export const aliveIds = (
  items: { id: string }[] | undefined, known: boolean, batch: ScanBatch | null,
): ReadonlySet<string> | null =>
  items && known && batch?.phase === 'done' ? new Set(items.map(i => i.id)) : null;

/** Повторы, которые показывает полоса: снимок загрузки, сверенный со списком, когда тот известен. */
export const shownRepeats = (batch: ScanBatch, alive: ReadonlySet<string> | null): RepeatedScan[] =>
  alive ? without(batch.repeats, id => alive.has(id)) : batch.repeats;

/** Заголовок списка повторов в полосе; `null` — повторов нет. */
export function repeatsTitle(batch: ScanBatch, shown: RepeatedScan[] = batch.repeats): string | null {
  const n = shown.length;
  if (n === 0) return null;
  return batch.phase === 'done' && n > 1 && n === batch.total
    ? `Все ${ruCount(n, 'файл', 'файла', 'файлов')} уже были приложены раньше — черновики всё равно заведены:`
    : `Уже были приложены — черновики всё равно заведены (${n}):`;
}

/** Что сказано о прежнем счёте: слова перед ссылкой и сама ссылка. */
export function repeatWords(repeat: RepeatedScan): { lead: string; link: string; tail: string } {
  const [first, ...rest] = repeat.others;
  const more = rest.length > 0 ? ` (и ещё ${rest.length})` : '';
  return first.inBatch && first.fileName
    ? { lead: 'тот же файл, что ', link: `«${first.fileName}»`, tail: ` из этой же загрузки${more}` }
    : { lead: 'уже приложен к счёту ', link: first.label, tail: more };
}

/** Строка полосы об отборе «только заведённые этой загрузкой». */
export interface UploadLine {
  /** Что сказано перед действием; пусто — говорить нечего, действие стоит одно. */
  text: string;
  action: 'only' | 'all';
  label: string;
  title?: string;
}

/**
 * Что полоса предлагает сделать со списком. Действие видно, как только заведён первый черновик, —
 * не дожидаясь конца загрузки: искать их приходится и до распознавания.
 *
 * `null` — предлагать нечего: ничего не заведено либо все заведённые из списка уже ушли. Включённый
 * отбор при этом остаётся снимаемым — иначе из пустого списка не было бы выхода в самой полосе.
 *
 * @param found сколько заведённых стоит в списке (под включённым отбором — сколько их показано, с
 * учётом поиска); `null` — не знаем: список не пришёл, перечитывается либо стоит под другим отбором.
 */
export function uploadLine(batch: ScanBatch, found: number | null, only: boolean): UploadLine | null {
  if (batch.created.length === 0) return null;
  if (only)
    return {
      text: found === null ? 'В списке только они.' : `В списке только они: ${found}.`,
      action: 'all', label: 'Показать все счета',
    };
  // Пока загрузка идёт, список перечитывается не на каждый файл (ревью PR #1304): «ни одного в списке»
  // значит тогда «список отстал», а не «все удалены», — и действие прятать нельзя.
  if (found === 0 && batch.phase === 'done') return null;
  return {
    text: '', action: 'only', label: 'Показать только их',
    title: 'Только счета, заведённые этой загрузкой. Отбор и поиск будут сняты.',
  };
}

/** Пустой список под отбором загрузки: её счета удалены либо не подошли под поиск. */
export const uploadEmpty = (query: string): string => query.trim()
  ? `Среди счетов этой загрузки по запросу «${query.trim()}» ничего нет.`
  : 'Счетов этой загрузки в списке не осталось.';

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
  // Во время проверки не отправлялось ничего: первого «мог дойти» нет.
  const sending = batch.phase !== 'checking';
  const lost: RejectedScan[] = batch.pending.map((name, i) => ({
    name, retry: null,
    reason: sending && i === 0
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
    // Копия, записанная до появления повторов (и придержанных), этих полей не несёт.
    const repeats = (stored.repeats ?? []).filter(r => Array.isArray(r.others));
    const held = (stored.held ?? []).map(h => ({ ...h, file: null }));
    return interrupted({
      ...EMPTY_EXTRAS, ...stored, repeats, held, rejected: stored.rejected.map(r => ({ ...r, retry: null })),
    }, 'страница перезагружена');
  } catch { return null; }
}

let state: ScanBatch | null = restore();
/** Номер запуска: цикл отправки, начатый до обрыва, после него в состояние не пишет. */
let generation = 0;
const listeners = new Set<() => void>();

function put(next: ScanBatch | null) {
  state = next;
  try {
    if (next) tab()?.setItem(STORED, JSON.stringify({
      ...next, rejected: next.rejected.map(r => ({ ...r, retry: null })), held: next.held.map(h => ({ ...h, file: null })),
    }));
    else tab()?.removeItem(STORED);
  } catch { /* хранилище недоступно — полоса просто не переживёт перезагрузку */ }
  listeners.forEach(l => l());
}

const patch = (change: Partial<ScanBatch>) => { if (state) put({ ...state, ...change }); };

/** Пока файлы не отправлены, закрытие вкладки спрашивает: недогруженное пропало бы молча. */
function warnOnLeave(e: BeforeUnloadEvent) { e.preventDefault(); }

/** Что отправляется: файл и что о нём известно с проверки. */
interface Outgoing {
  file: File;
  print: string | null;
  /** Сознательный повтор: двойники человеку показаны. */
  acceptRepeat?: boolean;
  /** Двойники, известные до отправки: найденный потом новый свой двойник — этот же черновик. */
  known?: string[];
  /** Повтор после «ответ не пришёл»: сначала спросить по отпечатку. */
  lost?: boolean;
}

async function run(items: Outgoing[], port: BatchPort) {
  const mine = ++generation;
  const alive = () => mine === generation && state !== null;
  // Через globalThis: логику гоняют и без окна (тесты), а там слушать нечего.
  globalThis.addEventListener?.('beforeunload', warnOnLeave as EventListener);
  try {
    for (const item of items) {
      const { file } = item;
      if (!alive()) return;
      const now = state!;
      const settle = (change: Partial<ScanBatch>) =>
        patch({ settled: state!.settled + 1, pending: state!.pending.slice(1), ...change });
      const reject = (reason: string, retry: boolean, lost?: RejectedScan['lost']) =>
        settle({ rejected: [...state!.rejected, { name: file.name, reason, retry: retry ? file : null, ...(lost ? { lost } : {}) }] });

      if (now.phase === 'stopping' || now.halted) { reject(NOT_SENT, true); continue; }
      // «Мог завестись» (D3 у 14): прежде чем слать второй раз, спросить, не завёлся ли он тогда.
      if (item.lost && item.print) {
        const found = await lostDraft(item.print, item.known ?? [], port);
        if (!alive()) return;
        if (found) {
          settle({ created: [...state!.created, found], recovered: [...state!.recovered, file.name] });
          continue;
        }
      }
      try {
        const { id, sameFile } = await port.send(file, item.acceptRepeat ? { acceptRepeat: true } : undefined);
        // За время запроса пакет могли оборвать (вход завершился) — тогда исход уже записан обрывом.
        if (!alive()) return;
        // Сознательный повтор строкой «уже были приложены» не называется: на этот вопрос человек ответил.
        const others = item.acceptRepeat ? [] : sameFile.map(s => ({
          id: s.id, label: sameFileLabel(s), fileName: s.scanFileName, inBatch: state!.created.includes(s.id),
        }));
        settle({
          created: [...state!.created, id],
          repeats: others.length > 0 ? [...state!.repeats, { name: file.name, id, others }] : state!.repeats,
        });
        if (state!.created.length % REFRESH_EVERY === 0) port.refresh();
      } catch (e) {
        if (!alive()) return;
        const { reason, retry, halt } = failure(e);
        const status = (e as { response?: { status?: number } })?.response?.status;
        const lost = status === undefined && item.print ? { print: item.print, known: item.known ?? [] } : undefined;
        reject(reason, retry, lost);
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
 * <p>Сначала проверка всего броска (D3 у 14): отказы предпроверки, повторы внутри броска, придержанные —
 * и только потом отправка. Ход «N из M» и «Остановить» начинаются после неё.</p>
 *
 * @param folders имена брошенных папок: внутрь не заходим, а молча пропустить — значит потерять.
 */
export function startBatch(files: File[], folders: string[], port: BatchPort) {
  if (!idle()) return;
  const count = files.length + folders.length;
  const refused = tooMany(count);
  if (refused) {
    put({
      owner: port.owner, phase: 'done', total: 0, settled: 0, created: [], repeats: [], rejected: [], pending: [],
      halted: null, refused, ...EMPTY_EXTRAS,
    });
    return;
  }
  put({
    owner: port.owner, phase: 'checking', total: count, settled: 0, created: [], repeats: [], rejected: [],
    pending: [...inOrder(files).map(f => f.name), ...folders], halted: null, refused: null, ...EMPTY_EXTRAS,
  });
  const mine = generation;
  void checkDrop(files, folders, port).then(checked => {
    // Вход завершился, пока шла проверка: исход уже записан обрывом.
    if (mine !== generation || state?.phase !== 'checking') return;
    const rejected: RejectedScan[] = checked.refused.map(r => ({ ...r, retry: null }));
    put({
      ...state, phase: 'running', total: rejected.length + checked.send.length, settled: rejected.length, rejected,
      pending: checked.send.map(s => s.file.name), unchecked: checked.unchecked, doubled: checked.doubled,
      held: checked.held.map(h => ({ name: h.file.name, file: h.file, print: h.print, twins: h.twins })),
    });
    void run(checked.send.map(s => ({ file: s.file, print: s.print, known: [] })), port);
  });
}

/**
 * Один файл, по которому человек не решил (окно повтора не открылось — он печатал, Э16 у 14), — придержанной
 * строкой в полосе пакета: снэкбар «Решить» живёт обычный срок, а решение не должно пропасть вместе с ним.
 * К законченному пакету того же человека строка добавляется, нового пакета не заводит.
 */
export function holdFile(held: HeldFile, owner: string) {
  const row: HeldScan = { name: held.file.name, file: held.file, print: held.print, twins: held.twins };
  if (state && state.phase !== 'done') return;
  if (state && state.owner === owner) { patch({ held: [...state.held.filter(h => h.print !== held.print), row] }); return; }
  put({
    owner, phase: 'done', total: 0, settled: 0, created: [], repeats: [], rejected: [], pending: [], halted: null,
    refused: null, ...EMPTY_EXTRAS, held: [row],
  });
}

/** Решено по придержанному («Не заводить» в окне, открытом из снэкбара) — строка уходит. */
export function releaseHeld(print: string) {
  if (!state || !state.held.some(h => h.print === print)) return;
  const held = state.held.filter(h => h.print !== print);
  // Пакет, в котором ничего, кроме этой строки, не было, уходит целиком: говорить ему больше не о чем.
  if (held.length === 0 && state.total === 0 && state.phase === 'done' && !state.refused) put(null);
  else patch({ held });
}

/** «Завести всё равно» (D5 у 14): один черновик как сознательный повтор; строка уходит из придержанных. */
export function acceptHeld(print: string, port: BatchPort) {
  if (!state || state.phase !== 'done' || state.owner !== port.owner) return;
  const held = state.held.find(h => h.print === print);
  if (!held?.file) return;
  put({
    ...state, phase: 'running', halted: null, total: state.total + 1, pending: [held.name],
    held: state.held.filter(h => h !== held),
  });
  void run([{ file: held.file, print: held.print, acceptRepeat: true, known: held.twins.map(t => t.id) }], port);
}

/** Текущий запрос не рвётся: оборванный не скажет, завёлся ли черновик. Не уходят только следующие. */
export function stopBatch() {
  if (state?.phase === 'running') patch({ phase: 'stopping' });
}

/** Ещё раз — только те, у кого повтор может кончиться иначе; счёт пакета продолжается, а не начинается заново. */
export function retryRejected(port: BatchPort) {
  if (!state || state.phase !== 'done' || state.owner !== port.owner) return;
  const again = state.rejected.filter(r => r.retry);
  if (again.length === 0) return;
  put({
    ...state, phase: 'running', halted: null, settled: state.settled - again.length,
    rejected: state.rejected.filter(r => !r.retry), pending: again.map(r => r.name),
  });
  void run(again.map(r => ({
    file: r.retry!, print: r.lost?.print ?? null, known: r.lost?.known ?? [], lost: !!r.lost,
  })), port);
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
