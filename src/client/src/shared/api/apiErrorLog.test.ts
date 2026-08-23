import { describe, expect, it, beforeEach } from 'vitest';
import { clearApiErrorLog, lastApiError, recentApiErrors, recordApiError } from './apiErrorLog';

/**
 * Кольцевой буфер ошибок API (issue #834).
 *
 * Проверяется потому, что растущий без предела буфер не падает и не мешает — он тихо тащит в
 * сообщение сотни строк и однажды упирается в потолок техблока на сервере, где его целиком
 * заменяют отметкой «не сохранён». То есть ошибка здесь выглядит как потеря контекста, а не как
 * дефект буфера. Ровно так же выглядит и обратный перекос — слишком короткий буфер: пока сервер
 * недоступен, фоновые опросы вытесняют из него запрос, о котором человек как раз пишет.
 */
describe('apiErrorLog', () => {
  beforeEach(() => clearApiErrorLog());

  const at = (n: number) => ({ at: `12:00:0${n}`, method: 'GET', url: `/x/${n}`, status: 500 });

  it('хранит последние пятнадцать и выбрасывает старые', () => {
    for (let i = 0; i < 20; i++) recordApiError({ ...at(0), url: `/x/${i}` });

    const kept = recentApiErrors();
    expect(kept).toHaveLength(15);
    // Остаются ПОСЛЕДНИЕ: сообщают о том, что только что случилось.
    expect(kept[0].url).toBe('/x/5');
    expect(kept[14].url).toBe('/x/19');
  });

  it('повторы одной и той же неудачи схлопываются в одну запись со счётчиком', () => {
    // Так выглядит недоступный сервер: фоновые опросы уведомлений и задач бьют в те же адреса
    // каждые 10–20 секунд. Без схлопывания они забили бы буфер целиком за полминуты.
    for (let i = 0; i < 30; i++) {
      recordApiError({ at: '12:00:00', method: 'GET', url: '/notifications', status: 0 });
      recordApiError({ at: '12:00:00', method: 'GET', url: '/jobs/active', status: 0 });
    }
    recordApiError({ at: '12:00:31', method: 'PUT', url: '/documents/1', status: 500, traceId: 'A' });

    const kept = recentApiErrors();
    expect(kept).toHaveLength(3);
    expect(kept.find(e => e.url === '/notifications')?.count).toBe(30);
    // Запрос, о котором человек пишет, на месте — а без схлопывания его бы уже вытеснило.
    expect(kept.at(-1)?.traceId).toBe('A');
  });

  it('схлопнутая запись несёт время и идентификатор ПОСЛЕДНЕЙ попытки', () => {
    recordApiError({ at: '12:00:01', method: 'GET', url: '/x', status: 500, traceId: 'первый' });
    recordApiError({ at: '12:00:09', method: 'GET', url: '/x', status: 500, traceId: 'последний' });

    const one = recentApiErrors()[0];
    // Искать в журнале будут по нему: он ближе всего к моменту, когда человек нажал «Сообщить».
    expect(one.traceId).toBe('последний');
    expect(one.at).toBe('12:00:09');
    expect(one.count).toBe(2);
  });

  it('разные коды ответа на одном адресе — разные записи', () => {
    recordApiError({ at: '12:00:01', method: 'GET', url: '/x', status: 500 });
    recordApiError({ at: '12:00:02', method: 'GET', url: '/x', status: 404 });
    expect(recentApiErrors()).toHaveLength(2);
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
