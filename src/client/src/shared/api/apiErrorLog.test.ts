import { describe, expect, it, beforeEach } from 'vitest';
import { clearApiErrorLog, lastApiError, recentApiErrors, recordApiError } from './apiErrorLog';

/**
 * Кольцевой буфер ошибок API (issue #834).
 *
 * Проверяется потому, что растущий без предела буфер не падает и не мешает — он тихо тащит в
 * сообщение сотни строк и однажды упирается в потолок техблока на сервере, где его целиком
 * заменяют отметкой «не сохранён». То есть ошибка здесь выглядит как потеря контекста, а не как
 * дефект буфера.
 */
describe('apiErrorLog', () => {
  beforeEach(() => clearApiErrorLog());

  const at = (n: number) => ({ at: `12:00:0${n}`, method: 'GET', url: `/x/${n}`, status: 500 });

  it('хранит последние десять и выбрасывает старые', () => {
    for (let i = 0; i < 14; i++) recordApiError({ ...at(0), url: `/x/${i}` });

    const kept = recentApiErrors();
    expect(kept).toHaveLength(10);
    // Остаются ПОСЛЕДНИЕ: сообщают о том, что только что случилось.
    expect(kept[0].url).toBe('/x/4');
    expect(kept[9].url).toBe('/x/13');
  });

  it('последняя запись — та, которой предзаполняется форма с тоста', () => {
    expect(lastApiError()).toBeNull();
    recordApiError({ ...at(1), traceId: 'A' });
    recordApiError({ ...at(2), traceId: 'B' });
    expect(lastApiError()?.traceId).toBe('B');
  });

  it('отдаёт копию: правка снаружи не трогает буфер', () => {
    recordApiError(at(1));
    recentApiErrors().push(at(2));
    expect(recentApiErrors()).toHaveLength(1);
  });
});
