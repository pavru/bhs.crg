import { describe, it, expect } from 'vitest';
import { apiError, withBlobErrorBody } from './apiError';

/**
 * Тело отказа, пришедшее блобом (запросы с `responseType: 'blob'` — выгрузки).
 *
 * Нашло ревью PR #1058: сервер отказывает выгрузке источника с негодной настройкой (issue #966) и
 * пишет человеку, ЧТО именно исправить, — а на экране появлялось «Request failed with status code
 * 409», потому что разбор ошибок видел на месте тела объект Blob.
 */
describe('withBlobErrorBody', () => {
  const reject = (data: unknown) => Promise.reject({ response: { data } });

  it('разворачивает JSON-тело отказа из блоба', async () => {
    const body = JSON.stringify({ error: 'Отбор строк источника «Материалы» не применён…', traceId: null });
    const e = await withBlobErrorBody(reject(new Blob([body], { type: 'application/json' }))).catch((x: unknown) => x);

    expect((e as { response: { data: { error: string } } }).response.data.error)
      .toContain('Отбор строк источника');
    expect(apiError(e)).toContain('Отбор строк источника');
  });

  it('не-JSON блоб отдаёт текстом — сырую строку разбор тоже умеет', async () => {
    const e = await withBlobErrorBody(reject(new Blob(['Источник не найден']))).catch((x: unknown) => x);
    expect(apiError(e)).toBe('Источник не найден');
  });

  it('обычное тело не трогает', async () => {
    const e = await withBlobErrorBody(reject({ error: 'Уже разобрано' })).catch((x: unknown) => x);
    expect(apiError(e)).toBe('Уже разобрано');
  });

  it('успешный ответ проходит насквозь', async () => {
    expect(await withBlobErrorBody(Promise.resolve('данные'))).toBe('данные');
  });
});
