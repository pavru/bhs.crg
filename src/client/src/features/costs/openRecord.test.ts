import { describe, expect, it } from 'vitest';
import { openRecord, type RecordRead } from './openRecord';

const read = (over: Partial<RecordRead<string>> = {}): RecordRead<string> =>
  ({ isPending: false, isError: false, data: undefined, error: null, ...over });
const refused = (status: number) => ({ response: { status } });
const offline = new Error('Network Error');

describe('openRecord', () => {
  it('ничего не выбрано', () => {
    expect(openRecord(null, read({ data: 'счёт' }), false)).toEqual({ kind: 'none' });
  });

  it('выбран и читается', () => {
    expect(openRecord('1', read({ isPending: true }), false)).toEqual({ kind: 'loading' });
  });

  it('прочитан — открыт', () => {
    expect(openRecord('1', read({ data: 'счёт' }), false))
      .toEqual({ kind: 'open', id: '1', data: 'счёт', gone: false, rereadFailed: null });
  });

  // Сторож задачи (#1287): раньше эта ошибка снимала форму вместе с набранным.
  it('перечитать не удалось, а документ в руках — форма остаётся, отказ назван', () => {
    expect(openRecord('1', read({ data: 'счёт', isError: true, error: offline }), false))
      .toEqual({ kind: 'open', id: '1', data: 'счёт', gone: false, rereadFailed: offline });
  });

  it('перечитывание ответило «нет такого» — документ удалили, пока он был открыт', () => {
    expect(openRecord('1', read({ data: 'счёт', isError: true, error: refused(404) }), false))
      .toEqual({ kind: 'open', id: '1', data: 'счёт', gone: true, rereadFailed: null });
  });

  it('узнанное «документа нет» переживает следующую ошибку — обрыв связи его не отменяет', () => {
    expect(openRecord('1', read({ data: 'счёт', isError: true, error: offline }), true))
      .toEqual({ kind: 'open', id: '1', data: 'счёт', gone: true, rereadFailed: null });
  });

  // «Ещё не знаем» — не «пусто»: без данных формы нет ни при какой ошибке.
  it('не прочитан ни разу — отказ чтения, а не форма', () => {
    expect(openRecord('1', read({ isError: true, error: offline }), false)).toEqual({ kind: 'unread', id: '1', error: offline });
  });

  it('не прочитан ни разу и «нет такого» — мёртвая ссылка', () => {
    expect(openRecord('1', read({ isError: true, error: refused(404) }), false)).toEqual({ kind: 'missing', id: '1' });
  });

  it('про документ, которого в руках нет, «уже знаем, что его нет» формы не даёт', () => {
    expect(openRecord('1', read({ isError: true, error: offline }), true)).toEqual({ kind: 'unread', id: '1', error: offline });
  });

  // Документ вернули (восстановлением из копии), либо «нет такого» было чужим: прочитался — значит, есть.
  it('узнанное «документа нет» удачное чтение отменяет', () => {
    expect(openRecord('1', read({ data: 'счёт' }), true))
      .toEqual({ kind: 'open', id: '1', data: 'счёт', gone: false, rereadFailed: null });
  });

  // Право чтения отозвали, пока документ открыт: это не обрыв связи — данные с экрана уходят.
  it('перечитывание ответило отказом доступа — форма снимается, а не остаётся с «Повторить»', () => {
    const denied = refused(403);
    expect(openRecord('1', read({ data: 'счёт', isError: true, error: denied }), false))
      .toEqual({ kind: 'unread', id: '1', error: denied });
  });
});
