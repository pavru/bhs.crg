import { beforeEach, describe, it, expect } from 'vitest';
import {
  MAX_BYTES, MAX_FILES, batchTitle, currentBatch, dismissBatch, failure, inOrder, precheck, resetBatchForTests,
  retryRejected, retryable, startBatch, stopBatch, tooMany,
} from './scanBatch';

/** Пакет сканов (issue #1093): непринятый файл не исчезает, а «ответ не пришёл» не выдаётся за «не заведён». */

const pdf = (name: string, size = 10) => {
  const file = new File(['x'], name, { type: 'application/pdf' });
  Object.defineProperty(file, 'size', { value: size });
  return file;
};

const refusal = (status: number, error: string) => ({ response: { status, data: { error } } });

/** Дождаться конца пакета: отправка идёт своим ходом, вне теста. */
async function settled() {
  for (let i = 0; i < 200 && currentBatch()?.phase !== 'done'; i++) await Promise.resolve();
  return currentBatch()!;
}

beforeEach(() => resetBatchForTests());

describe('файл до отправки', () => {
  it('не тот вид, пустой и слишком большой отсеиваются с причиной', () => {
    expect(precheck(new File(['x'], 'a.docx', { type: 'application/msword' }))).toBe('не PDF, PNG или JPEG');
    expect(precheck(pdf('a.pdf', 0))).toBe('файл пуст');
    expect(precheck(pdf('a.pdf', MAX_BYTES + 1))).toBe('больше 50 МБ');
    expect(precheck(pdf('a.pdf', MAX_BYTES))).toBeNull();
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
  it('ответ не пришёл — не «не заведён»: черновик мог завестись', () => {
    const lost = failure(new Error('Network Error'));
    expect(lost.reason).toContain('черновик мог завестись');
    expect(lost).toMatchObject({ retry: true, halt: null });
  });

  it('отказ про сам файл повторять незачем, сбой сервера — есть смысл', () => {
    expect(failure(refusal(400, 'Файл пуст.'))).toEqual({ reason: 'сервер отказал: Файл пуст.', retry: false, halt: null });
    expect(failure(refusal(413, ''))).toMatchObject({ reason: 'больше, чем принимает сервер', retry: false });
    expect(failure(refusal(500, 'Сбой.'))).toMatchObject({ reason: 'сервер отказал: Сбой.', retry: true, halt: null });
  });

  it('вход истёк, права нет, частота превышена — пакет останавливается', () => {
    for (const status of [401, 403, 429])
      expect(failure(refusal(status, 'Нельзя.'))).toMatchObject({ halt: 'Нельзя.', retry: true });
  });
});

describe('пакет', () => {
  it('десять файлов — десять черновиков, по порядку имён', async () => {
    const sent: string[] = [];
    const files = Array.from({ length: 10 }, (_, i) => pdf(`скан ${10 - i}.pdf`));
    startBatch(files, [], async file => { sent.push(file.name); return `id-${file.name}`; });

    const batch = await settled();
    expect(batch.created).toHaveLength(10);
    expect(batch.rejected).toEqual([]);
    expect(sent[0]).toBe('скан 1.pdf');
    expect(sent[9]).toBe('скан 10.pdf');
    expect(batchTitle(batch)).toBe('Заведено 10 черновиков. Распознавание идёт — состояние в строках списка.');
  });

  it('непринятый файл назван с причиной, а остальные заведены', async () => {
    const files = [pdf('а.pdf'), new File(['x'], 'б.txt', { type: 'text/plain' }), pdf('в.pdf'), pdf('г.pdf')];
    startBatch(files, ['папка'], async file => {
      if (file.name === 'в.pdf') throw refusal(500, 'Хранилище не ответило.');
      return file.name;
    });

    const batch = await settled();
    expect(batch.created).toEqual(['а.pdf', 'г.pdf']);
    expect(batch.rejected.map(r => `${r.name}: ${r.reason}`)).toEqual([
      'папка: это папка — перетащите файлы из неё',
      'б.txt: не PDF, PNG или JPEG',
      'в.pdf: сервер отказал: Хранилище не ответило.',
    ]);
    expect(batchTitle(batch)).toBe('Заведено 2 из 5. Не принято 3:');
    // Повторить можно только то, что может кончиться иначе.
    expect(retryable(batch).map(f => f.name)).toEqual(['в.pdf']);
  });

  it('повтор шлёт только повторяемые и продолжает счёт пакета', async () => {
    let fail = true;
    const send = async (file: File) => {
      if (file.name === 'б.pdf' && fail) throw new Error('Network Error');
      return file.name;
    };
    startBatch([pdf('а.pdf'), pdf('б.pdf'), pdf('в.pdf', 0)], [], send);
    await settled();

    fail = false;
    retryRejected(send);
    const batch = await settled();
    expect(batch.created).toEqual(['а.pdf', 'б.pdf']);
    expect(batch.rejected.map(r => r.name)).toEqual(['в.pdf']);
    expect(batch).toMatchObject({ total: 3, settled: 3 });
  });

  it('«Остановить» не рвёт текущий файл, а остальные называет неотправленными', async () => {
    let release = (_: string) => {};
    const sent: string[] = [];
    startBatch([pdf('а.pdf'), pdf('б.pdf'), pdf('в.pdf')], [], file => {
      sent.push(file.name);
      return new Promise<string>(resolve => { release = resolve; });
    });
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

  it('отказ во входе останавливает пакет сам', async () => {
    const sent: string[] = [];
    startBatch([pdf('а.pdf'), pdf('б.pdf'), pdf('в.pdf')], [], async file => {
      sent.push(file.name);
      throw refusal(429, 'Слишком много запросов.');
    });

    const batch = await settled();
    expect(sent).toEqual(['а.pdf']);
    expect(batch.rejected).toHaveLength(3);
    expect(batchTitle(batch)).toBe('Загрузка остановлена: Слишком много запросов. Заведено 0 из 3.');
  });

  it('набор сверх предела не шлёт ничего', async () => {
    const sent: string[] = [];
    startBatch(Array.from({ length: MAX_FILES + 1 }, (_, i) => pdf(`${i}.pdf`)), [], async f => { sent.push(f.name); return ''; });

    expect(currentBatch()).toMatchObject({ phase: 'done', total: 0 });
    expect(currentBatch()!.refused).toContain('Ничего не загружено');
    expect(sent).toEqual([]);
  });

  it('пока пакет идёт, второй не начинается и полоса не закрывается', async () => {
    let release = (_: string) => {};
    startBatch([pdf('а.pdf')], [], () => new Promise<string>(resolve => { release = resolve; }));
    startBatch([pdf('б.pdf'), pdf('в.pdf')], [], async () => 'x');
    dismissBatch();
    expect(currentBatch()).toMatchObject({ total: 1, phase: 'running' });

    release('а');
    await settled();
    dismissBatch();
    expect(currentBatch()).toBeNull();
  });
});
