import { AxiosError } from 'axios';
import { describe, expect, it } from 'vitest';
import { purchaseRefusal } from './purchaseRequests';

describe('отказы заявок', () => {
  it.each([400, 403, 409, 422])('показывает текст сервера при %s', status => {
    const error = Object.assign(new AxiosError(), { response: { status, data: { error: 'Причина из API' } } });
    expect(purchaseRefusal(error)).toEqual({ conflict: status === 409, message: 'Причина из API' });
  });

  it('отвечает по-русски при 403 без тела', () => {
    const error = Object.assign(new AxiosError(), { response: { status: 403 } });
    expect(purchaseRefusal(error).message).toBe('Для действия не хватает права.');
  });
});
