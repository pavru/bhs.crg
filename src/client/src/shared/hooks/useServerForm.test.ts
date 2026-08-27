import { describe, it, expect } from 'vitest';
import { resolveServerForm } from './useServerForm';

interface Schedule { enabled: boolean; time: string }

describe('resolveServerForm', () => {
  const server: Schedule = { enabled: true, time: '03:00' };
  const asForm = (s: Schedule) => ({ ...s });

  it('без правки показывает серверное значение', () => {
    expect(resolveServerForm(server, null, asForm(server))).toEqual({ enabled: true, time: '03:00' });
  });

  it('правку показывает, пока она отвечает нынешнему серверному значению', () => {
    const edited = { from: server, value: { enabled: false, time: '03:00' } };
    expect(resolveServerForm(server, edited, asForm(server)))
      .toEqual({ enabled: false, time: '03:00' });
  });

  it('другое серверное значение замещает правку — расписание могли поменять из другой вкладки', () => {
    const edited = { from: server, value: { enabled: false, time: '03:00' } };
    const fresh: Schedule = { enabled: true, time: '05:30' };
    expect(resolveServerForm(fresh, edited, asForm(fresh)))
      .toEqual({ enabled: true, time: '05:30' });
  });

  it('сравнение по ссылке: равный по содержимому, но ДРУГОЙ объект правку сбивает', () => {
    // Так и задумано. React Query при перечитывании без изменений отдаёт ПРЕЖНЮЮ ссылку
    // (structural sharing), поэтому до этой ветки доходит только настоящая смена данных.
    // Если источник ссылку не бережёт, форма будет сбрасываться на каждый ответ — и это будет
    // видно сразу, а не однажды и молча.
    const edited = { from: server, value: { enabled: false, time: '03:00' } };
    const sameContent: Schedule = { enabled: true, time: '03:00' };
    expect(resolveServerForm(sameContent, edited, asForm(sameContent)))
      .toEqual({ enabled: true, time: '03:00' });
  });

  it('правка от ПРЕЖНЕГО серверного значения не воскресает, когда сервер вернулся к нему же', () => {
    // Ссылка другая, даже если содержимое совпало с исходным: правка отброшена окончательно.
    const edited = { from: server, value: { enabled: false, time: '03:00' } };
    const backAgain: Schedule = { enabled: true, time: '03:00' };
    expect(resolveServerForm(backAgain, edited, asForm(backAgain))).toEqual(asForm(backAgain));
  });
});
