import { beforeEach, describe, it, expect } from 'vitest';
import type { FileKindsInfo } from '@/shared/api/fileKinds';
import {
  MAX_FILES, batchTitle, currentBatch, dismissBatch, endSession, failure, inOrder, interrupted,
  precheck, resetBatchForTests, retryRejected, retryable, startBatch, stopBatch, tooMany, forgetCreated, uploadEmpty, uploadLine, uploadedOf, repeatsTitle, repeatWords, aliveIds, shownRepeats,
  type BatchPort, type ScanBatch,
} from './scanBatch';

/** Пакет сканов (issue #1093): непринятый файл не исчезает, а «ответ не пришёл» не выдаётся за «не заведён». */

const pdf = (name: string, size = 10) => {
  const file = new File(['x'], name, { type: 'application/pdf' });
  Object.defineProperty(file, 'size', { value: size });
  return file;
};

const refusal = (status: number, error: string) => ({ response: { status, data: { error } } });

const MAX_BYTES = 50 * 1024 * 1024;
let refreshed = 0;
/** Реестр, каким его отдаёт сервер: что распознаётся, решает он, а не экран. */
const kinds: FileKindsInfo = {
  unknown: 'application/octet-stream',
  maxBytes: MAX_BYTES,
  kinds: [
    { mime: 'application/pdf', label: 'PDF', extensions: ['.pdf'], aliases: ['application/x-pdf'], view: 'pdf', recognized: true },
    { mime: 'image/png', label: 'PNG', extensions: ['.png'], aliases: [], view: 'image', recognized: true },
    { mime: 'image/jpeg', label: 'JPEG', extensions: ['.jpg', '.jpeg'], aliases: ['image/pjpeg'], view: 'image', recognized: true },
    { mime: 'image/webp', label: 'WebP', extensions: ['.webp'], aliases: [], view: 'image', recognized: false },
  ],
};
type Sent = Awaited<ReturnType<BatchPort['send']>>;
/** Отправка в тестах отвечает идентификатором; ответ с повтором называет его целиком. */
const port = (send: (file: File) => Promise<string | Sent>, owner = 'я'): BatchPort => ({
  owner, kinds, refresh: () => { refreshed++; },
  send: async file => { const got = await send(file); return typeof got === 'string' ? { id: got, sameFile: [] } : got; },
});

/** Дождаться конца пакета: отправка идёт своим ходом, вне теста. */
async function settled() {
  for (let i = 0; i < 400 && currentBatch()?.phase !== 'done'; i++) await Promise.resolve();
  return currentBatch()!;
}

beforeEach(() => { resetBatchForTests(); refreshed = 0; });

describe('файл до отправки', () => {
  it('не тот вид, пустой и слишком большой отсеиваются с причиной', () => {
    expect(precheck(new File(['x'], 'a.docx', { type: 'application/msword' }), kinds)).toBe('не PDF, PNG или JPEG');
    // Вид, который реестр знает, но читаемым не называет, — тоже отказ: показать его можно, распознать нет.
    expect(precheck(new File(['x'], 'a.webp', { type: 'image/webp' }), kinds)).toBe('не PDF, PNG или JPEG');
    // Вид определяет сервер (issue #1265): файл, который браузер никак не назвал, уходит к нему.
    expect(precheck(new File(['x'], 'скан'), kinds)).toBeNull();
    expect(precheck(new File(['x'], 'скан.bin', { type: 'application/octet-stream' }), kinds)).toBeNull();
    expect(precheck(pdf('a.pdf', 0), kinds)).toBe('файл пуст');
    expect(precheck(pdf('a.pdf', MAX_BYTES + 1), kinds)).toBe('больше 50 МБ');
    expect(precheck(pdf('a.pdf', MAX_BYTES), kinds)).toBeNull();
  });

  // Перечень читаемого — серверный: что в реестре стало читаемым, то экран и пропускает, и называет.
  it('что распознаётся, решает реестр, а не экран', () => {
    const wider: FileKindsInfo = {
      ...kinds, kinds: kinds.kinds.map(kind => ({ ...kind, recognized: true })),
    };
    expect(precheck(new File(['x'], 'a.webp', { type: 'image/webp' }), wider)).toBeNull();
    expect(precheck(new File(['x'], 'a.gif', { type: 'image/gif' }), wider)).toBe('не PDF, PNG, JPEG или WebP');
  });

  // Реестр не пришёл — по виду не отсекается ничего: решит сервер, а пустой и огромный файл
  // отсекаются по-прежнему.
  it('без реестра вид и размер файла решает сервер', () => {
    expect(precheck(new File(['x'], 'a.docx', { type: 'application/msword' }), undefined)).toBeNull();
    expect(precheck(pdf('a.pdf', MAX_BYTES + 1), undefined)).toBeNull();
    expect(precheck(pdf('a.pdf', 0), undefined)).toBe('файл пуст');
  });

  // Предел — из реестра, а не число экрана: сменился на сервере — сменился и здесь, вместе с текстом.
  it('предел размера называет реестр', () => {
    const tight: FileKindsInfo = { ...kinds, maxBytes: 20 * 1024 * 1024 };
    expect(precheck(pdf('a.pdf', 20 * 1024 * 1024 + 1), tight)).toBe('больше 20 МБ');
    expect(precheck(pdf('a.pdf', 20 * 1024 * 1024), tight)).toBeNull();
  });

  // Браузер зовёт один вид по-разному (зависит от записей типов в системе), а сервер смотрит на
  // содержимое: файл под другим именем того же вида обязан дойти до него.
  it('вид под другим названием браузера не отсекается', () => {
    expect(precheck(new File(['x'], 'a.pdf', { type: 'application/x-pdf' }), kinds)).toBeNull();
    expect(precheck(new File(['x'], 'a.jpg', { type: 'image/pjpeg' }), kinds)).toBeNull();
  });

  it('порядок — по имени, числа числами', () => {
    expect(inOrder([pdf('скан 10.pdf'), pdf('скан 2.pdf'), pdf('акт.pdf')]).map(f => f.name))
      .toEqual(['акт.pdf', 'скан 2.pdf', 'скан 10.pdf']);
  });

  it('набор больше предела отвергается целиком', () => {
    expect(tooMany(MAX_FILES)).toBeNull();
    expect(tooMany(214)).toBe('Выбрано 214 файлов — за раз принимается до 50. Ничего не загружено.');
  });
});

describe('отказ запроса', () => {
  it('ответ не пришёл — не «не заведён»: черновик мог завестись, а слать дальше некуда', () => {
    const lost = failure(new Error('Network Error'));
    expect(lost.reason).toContain('черновик мог завестись');
    expect(lost).toMatchObject({ retry: true, halt: 'сервер не отвечает' });
  });

  it('про сам файл говорят только 400 и 413 — пакет после них идёт дальше', () => {
    expect(failure(refusal(400, 'Файл пуст.'))).toEqual({ reason: 'сервер отказал: Файл пуст.', retry: false, halt: null });
    expect(failure(refusal(413, ''))).toEqual({ reason: 'больше, чем принимает сервер', retry: false, halt: null });
  });

  it('отказ, одинаковый для любого файла, останавливает пакет', () => {
    for (const status of [403, 409, 429, 500, 502, 503])
      expect(failure(refusal(status, 'Нельзя.'))).toMatchObject({ halt: 'Нельзя.', retry: true });
  });
});

describe('пакет', () => {
  it('десять файлов — десять черновиков, по порядку имён, а список перечитан не десять раз', async () => {
    const sent: string[] = [];
    const files = Array.from({ length: 10 }, (_, i) => pdf(`скан ${10 - i}.pdf`));
    startBatch(files, [], port(async file => { sent.push(file.name); return `id-${file.name}`; }));

    const batch = await settled();
    expect(batch.created).toHaveLength(10);
    expect(batch.rejected).toEqual([]);
    expect(batch.pending).toEqual([]);
    expect(sent[0]).toBe('скан 1.pdf');
    expect(sent[9]).toBe('скан 10.pdf');
    expect(batchTitle(batch)).toBe('Заведено 10 черновиков. Распознавание идёт — состояние в строках списка.');
    // На пятом, на десятом и в конце.
    expect(refreshed).toBe(3);
  });

  it('непринятый файл назван с причиной, а остальные заведены', async () => {
    const files = [pdf('а.pdf'), new File(['x'], 'б.txt', { type: 'text/plain' }), pdf('в.pdf'), pdf('г.pdf')];
    startBatch(files, ['папка'], port(async file => {
      if (file.name === 'в.pdf') throw refusal(400, 'Файл не читается.');
      return file.name;
    }));

    const batch = await settled();
    expect(batch.created).toEqual(['а.pdf', 'г.pdf']);
    expect(batch.rejected.map(r => `${r.name}: ${r.reason}`)).toEqual([
      'папка: это папка — перетащите файлы из неё',
      'б.txt: не PDF, PNG или JPEG',
      'в.pdf: сервер отказал: Файл не читается.',
    ]);
    expect(batchTitle(batch)).toBe('Заведено 2 из 5. Не принято 3:');
    expect(retryable(batch)).toEqual([]);
  });

  it('повтор шлёт только повторяемые и продолжает счёт пакета', async () => {
    let fail = true;
    const mine = port(async file => {
      if (file.name === 'б.pdf' && fail) throw new Error('Network Error');
      return file.name;
    });
    startBatch([pdf('а.pdf'), pdf('б.pdf'), pdf('в.pdf', 0), pdf('г.pdf')], [], mine);
    const first = await settled();
    // Связи нет — пакет остановился сам: «г» не уходил, и «мог завестись» сказано только про «б».
    expect(first.rejected.map(r => r.name)).toEqual(['б.pdf', 'в.pdf', 'г.pdf']);
    expect(first.rejected[2].reason).toBe('не отправлен — загрузка остановлена');
    expect(batchTitle(first)).toBe('Загрузка остановлена: сервер не отвечает. Заведено 1 из 4.');

    fail = false;
    retryRejected(port(async () => 'чужой', 'другой'));
    expect(currentBatch()!.phase).toBe('done');
    retryRejected(mine);
    const batch = await settled();
    expect(batch.created).toEqual(['а.pdf', 'б.pdf', 'г.pdf']);
    expect(batch.rejected.map(r => r.name)).toEqual(['в.pdf']);
    expect(batch).toMatchObject({ total: 4, settled: 4, halted: null });
  });

  it('«Остановить» не рвёт текущий файл, а остальные называет неотправленными', async () => {
    let release = (_: string) => {};
    const sent: string[] = [];
    startBatch([pdf('а.pdf'), pdf('б.pdf'), pdf('в.pdf')], [], port(file => {
      sent.push(file.name);
      return new Promise<string>(resolve => { release = resolve; });
    }));
    await Promise.resolve();
    stopBatch();
    expect(batchTitle(currentBatch()!)).toBe('Останавливаем — догружается текущий файл…');
    release('а');

    const batch = await settled();
    expect(sent).toEqual(['а.pdf']);
    expect(batch.created).toEqual(['а']);
    expect(batch.rejected.map(r => r.reason)).toEqual(Array(2).fill('не отправлен — загрузка остановлена'));
    expect(retryable(batch)).toHaveLength(2);
  });

  it('отказ без точки на конце не сливается со следующей фразой', async () => {
    const sent: string[] = [];
    startBatch([pdf('а.pdf'), pdf('б.pdf'), pdf('в.pdf')], [], port(async file => {
      sent.push(file.name);
      throw { response: { status: 429 } };
    }));

    const batch = await settled();
    expect(sent).toEqual(['а.pdf']);
    expect(batch.rejected).toHaveLength(3);
    expect(batchTitle(batch)).toBe('Загрузка остановлена: отказ 429. Заведено 0 из 3.');
  });

  it('набор сверх предела не шлёт ничего', () => {
    const sent: string[] = [];
    startBatch(Array.from({ length: MAX_FILES + 1 }, (_, i) => pdf(`${i}.pdf`)), [],
      port(async f => { sent.push(f.name); return ''; }));

    expect(currentBatch()).toMatchObject({ phase: 'done', total: 0 });
    expect(currentBatch()!.refused).toContain('Ничего не загружено');
    expect(sent).toEqual([]);
  });

  it('пока пакет идёт, второй не начинается и полоса не закрывается', async () => {
    let release = (_: string) => {};
    startBatch([pdf('а.pdf')], [], port(() => new Promise<string>(resolve => { release = resolve; })));
    startBatch([pdf('б.pdf'), pdf('в.pdf')], [], port(async () => 'x'));
    dismissBatch();
    expect(currentBatch()).toMatchObject({ total: 1, phase: 'running' });

    release('а');
    await settled();
    dismissBatch();
    expect(currentBatch()).toBeNull();
  });
});

describe('счета этой загрузки в списке', () => {
  const made = (created: string[]): ScanBatch => ({
    owner: 'me', phase: 'done', total: created.length, settled: created.length, created,
    rejected: [], repeats: [], pending: [], halted: null, refused: null,
  });
  const list = ['x', 'b', 'y', 'a'].map(id => ({ id }));

  it('отбор — ровно заведённые, в порядке списка, а не файлов', () => {
    expect(uploadedOf(list, made(['a', 'b']))).toEqual([{ id: 'b' }, { id: 'a' }]);
  });

  it('удалённого счёта в отборе нет — число берётся из списка, а не из заведённых', () => {
    expect(uploadedOf(list, made(['a', 'gone']))).toEqual([{ id: 'a' }]);
  });

  it('список не пришёл — «не знаем», а не «ни одного»', () => {
    expect(uploadedOf(undefined, made(['a']))).toBeNull();
    expect(uploadedOf(list, null)).toBeNull();
  });

  it('полоса предлагает отбор, как только заведён первый черновик, — и до конца загрузки', () => {
    expect(uploadLine(made([]), 0, false)).toBeNull();
    expect(uploadLine({ ...made(['a']), phase: 'running' }, 1, false))
      .toMatchObject({ action: 'only', label: 'Показать только их' });
    // Список не пришёл — действие остаётся: «не знаем» не прячет его.
    expect(uploadLine(made(['a']), null, false)?.action).toBe('only');
    // Загрузка идёт, а список ещё не перечитан: «ни одного» значит «список отстал» — действие на месте.
    expect(uploadLine({ ...made(['a']), phase: 'running' }, 0, false)?.action).toBe('only');
  });

  it('удалённый в этой вкладке счёт уходит из заведённых, чужой идентификатор ничего не меняет', async () => {
    startBatch([pdf('1.pdf'), pdf('2.pdf')], [], port(async file => `id-${file.name}`));
    await settled();
    forgetCreated('нет такого');
    expect(currentBatch()!.created).toEqual(['id-1.pdf', 'id-2.pdf']);
    forgetCreated('id-1.pdf');
    expect(currentBatch()!.created).toEqual(['id-2.pdf']);
  });

  it('пустой список под отбором называет поиск, если пусто из-за него', () => {
    expect(uploadEmpty('')).toBe('Счетов этой загрузки в списке не осталось.');
    expect(uploadEmpty(' акт ')).toBe('Среди счетов этой загрузки по запросу «акт» ничего нет.');
  });

  it('все заведённые из списка ушли — предлагать нечего, а включённый отбор снять можно', () => {
    expect(uploadLine(made(['a']), 0, false)).toBeNull();
    expect(uploadLine(made(['a']), 0, true))
      .toEqual({ text: 'В списке только они: 0.', action: 'all', label: 'Показать все счета' });
  });

  it('включённый отбор называет число найденных, а не заведённых', () => {
    expect(uploadLine(made(['a', 'b', 'c']), 2, true)?.text).toBe('В списке только они: 2.');
    expect(uploadLine(made(['a']), null, true)?.text).toBe('В списке только они.');
  });
});

describe('пакет оборван извне', () => {
  it('вход завершился: остаток не уходит, а опоздавший ответ итог не переписывает', async () => {
    let release = (_: string) => {};
    const sent: string[] = [];
    startBatch([pdf('а.pdf'), pdf('б.pdf'), pdf('в.pdf')], [], port(file => {
      sent.push(file.name);
      return new Promise<string>(resolve => { release = resolve; });
    }));
    await Promise.resolve();
    endSession();
    release('опоздал');
    for (let i = 0; i < 20; i++) await Promise.resolve();

    const batch = currentBatch()!;
    expect(sent).toEqual(['а.pdf']);
    expect(batch).toMatchObject({ phase: 'done', created: [], settled: 3, halted: 'вход в систему завершился' });
    expect(batch.rejected.map(r => r.reason)).toEqual([
      'ответ не получен — вход в систему завершился; черновик мог завестись, проверьте список',
      'не отправлен — вход в систему завершился; выберите файл заново',
      'не отправлен — вход в систему завершился; выберите файл заново',
    ]);
    // Файлов больше нет — повторять нечем.
    expect(retryable(batch)).toEqual([]);
  });

  it('законченный пакет обрыв не трогает', () => {
    const done = { owner: 'я', phase: 'done', total: 1, settled: 1, created: ['x'], rejected: [], repeats: [], pending: [], halted: null, refused: null } as const;
    expect(interrupted({ ...done, created: ['x'], rejected: [], repeats: [], pending: [] }, 'страница перезагружена')).toMatchObject({ halted: null });
  });
});

describe('файл, уже приложенный к другому счёту', () => {
  const same = (id: string, number: string | null, scanFileName: string) =>
    ({ id, number, issuedOn: null, total: null, scanFileName, createdAt: '2026-10-12T11:02:07Z' });
  /** Два файла: первый новый, второй повторяет названные счета. */
  const twice = (...twins: ReturnType<typeof same>[]) => startBatch([pdf('1.pdf'), pdf('2.pdf')], [],
    port(async file => (file.name === '1.pdf' ? 'первый' : { id: 'новый', sameFile: twins })));

  it('повтор — не отказ: черновик заведён, а в полосе назван прежний счёт', async () => {
    twice(same('прежний', 'СЧ-7', 'старый.pdf'), same('ещё', null, 'x.pdf'));
    const batch = await settled();

    expect(batch.created).toEqual(['первый', 'новый']);
    expect(batch.rejected).toEqual([]);
    expect(batch.repeats).toMatchObject([{
      name: '2.pdf', id: 'новый',
      others: [{ id: 'прежний', label: '№ СЧ-7', fileName: 'старый.pdf', inBatch: false }, { id: 'ещё', inBatch: false }],
    }]);
    expect(repeatsTitle(batch)).toBe('Уже были приложены — черновики всё равно заведены (1):');
    expect(repeatWords(batch.repeats[0])).toEqual({ lead: 'уже приложен к счёту ', link: '№ СЧ-7', tail: ' (и ещё 1)' });
    // Заголовок полосы повторы в непринятые не записывает.
    expect(batchTitle(batch)).toContain('Заведено 2 черновика');
  });

  it('оба файла в одной загрузке: прежний назван именем файла', async () => {
    twice(same('первый', null, '1.pdf'));
    const batch = await settled();

    expect(batch.repeats[0].others[0].inBatch).toBe(true);
    expect(repeatWords(batch.repeats[0])).toEqual({ lead: 'тот же файл, что ', link: '«1.pdf»', tail: ' из этой же загрузки' });
    // Подпись без номера различает и имя файла, и секунды: черновики одной загрузки заведены в одну минуту.
    expect(batch.repeats[0].others[0].label).toMatch(/^без номера, «1\.pdf», заведён 12\.10\.2026, \d\d:02:07$/);
  });

  it('все файлы повторные — заголовок говорит это прямо', async () => {
    startBatch([pdf('1.pdf'), pdf('2.pdf')], [], port(async file =>
      ({ id: `новый-${file.name}`, sameFile: [same(`прежний-${file.name}`, null, file.name)] })));
    expect(repeatsTitle(await settled()))
      .toBe('Все 2 файла уже были приложены раньше — черновики всё равно заведены:');
  });

  it('удалён новый черновик — строка уходит; удалён прежний — уходит, только если двойников не осталось', async () => {
    twice(same('прежний', 'СЧ-7', 'старый.pdf'), same('ещё', 'СЧ-8', 'x.pdf'));
    await settled();
    // Старший двойник удалён, но остался другой: черновик по-прежнему лишний, и строка называет его.
    forgetCreated('прежний');
    expect(currentBatch()!.repeats).toMatchObject([{ id: 'новый', others: [{ id: 'ещё' }] }]);
    expect(repeatWords(currentBatch()!.repeats[0]).link).toBe('№ СЧ-8');
    forgetCreated('ещё');
    expect(currentBatch()).toMatchObject({ repeats: [], created: ['первый', 'новый'] });

    dismissBatch();
    twice(same('прежний', 'СЧ-7', 'старый.pdf'));
    await settled();
    forgetCreated('новый');
    expect(currentBatch()).toMatchObject({ repeats: [], created: ['первый'] });
  });

  it('счёт, удалённый соседом, из полосы уходит по списку — а пока список неизвестен, повторы на месте', async () => {
    twice(same('прежний', 'СЧ-7', 'старый.pdf'), same('ещё', 'СЧ-8', 'x.pdf'));
    const batch = await settled();
    const list = (...ids: string[]) => ids.map(id => ({ id }));

    // Не знаем — не прячем: список не пришёл, стоит под отбором сервера либо загрузка ещё идёт.
    expect(aliveIds(undefined, true, batch)).toBeNull();
    expect(aliveIds(list('новый'), false, batch)).toBeNull();
    expect(aliveIds(list('новый'), true, { ...batch, phase: 'running' })).toBeNull();
    expect(shownRepeats(batch, null)).toBe(batch.repeats);

    // Сосед удалил старшего двойника: называется следующий.
    const shown = shownRepeats(batch, aliveIds(list('новый', 'ещё'), true, batch));
    expect(shown.map(r => r.others.map(o => o.id))).toEqual([['ещё']]);
    // Удалил обоих либо сам новый черновик: звать некуда, строки нет — и заголовка тоже.
    expect(shownRepeats(batch, aliveIds(list('новый'), true, batch))).toEqual([]);
    expect(shownRepeats(batch, aliveIds(list('прежний', 'ещё'), true, batch))).toEqual([]);
    expect(repeatsTitle(batch, [])).toBeNull();
    // Снимок при этом цел: список мог отстать, и следующее чтение вернёт строку.
    expect(batch.repeats).toHaveLength(1);
  });
});
