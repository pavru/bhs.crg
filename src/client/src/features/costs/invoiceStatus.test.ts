import { describe, expect, it } from 'vitest';
import type { InvoiceReadinessGap, InvoiceView, PaymentView } from '@/shared/api/invoices';
import type { InvoiceRecognition } from '@/shared/api/invoiceRecognition';
import { bannerOf, blocksText, briefOf, statusOf, type StatusInput } from './invoiceStatus';

/**
 * Индикатор состояния счёта (costs-invoice-screen-11, задачи 1.1–1.4, ТЗ COST-6.4). Чем ломается: число
 * в индикаторе расходится с перечнем, одно расхождение названо дважды, отказ чтения молчит пустотой,
 * разобранный счёт говорит «Готов к «Разобран»», пункт без права ведёт в отказ.
 */

type ViewPatch = Omit<Partial<InvoiceView>, 'payment'> & { payment?: Partial<PaymentView>; gaps?: InvoiceReadinessGap[] | null; state?: string };

function view({ gaps = [], state = 'Черновик', payment, ...rest }: ViewPatch = {}): InvoiceView {
  return {
    id: 'i1', version: 'v1', documentTypeId: 't',
    requisites: { Состояние: state, Срок: '2026-10-03' },
    unconfirmed: [], duplicates: [], sameFile: [], lines: [],
    allocation: {
      allocated: true, unbalanced: [], lost: 0, discrepancy: null, tolerance: 1, withinTolerance: true, reconciled: false,
      document: { parts: [] }, stamp: 's',
    },
    payment: { paid: false, paidOn: null, document: null, paidAt: null, refusal: null, lockedBy: null, periods: [], refusalKind: null, ...payment },
    readiness: { required: [], gaps },
    updatedAt: '2026-11-07T09:04:00Z', createdAt: '2026-11-01T09:00:00Z',
    ...rest,
  } as unknown as InvoiceView;
}

const CAN = { edit: true, pay: true, allocate: true, correct: true, closePeriod: false, correctable: false, delete: true };

function input(v: InvoiceView, form: Partial<StatusInput['form']> = {}, recognition?: InvoiceRecognition, unread = false): StatusInput {
  return {
    view: v, recognition, recognitionUnread: unread,
    form: {
      gone: false, stale: { header: false, lines: false, correction: false }, orgsFailed: null, rereadFailed: null,
      hiddenByFilter: false, extrasFailed: null, matches: null, marks: [], correcting: false, can: CAN, ...form,
    },
  };
}

const failed = (reason: string): InvoiceRecognition => ({
  state: 'failed', reason, error: null, engine: null, progress: null, values: null, offers: null, lines: null, notes: [],
  startedAt: null, finishedAt: null, canStart: true, whyNot: null, parties: null, byFormerImage: false, unplaced: null,
} as InvoiceRecognition);

const DISCREPANCY: InvoiceReadinessGap = {
  kind: 'total', label: 'сумма строк расходится с суммой к оплате на 38400.50', field: 'Итого', blocks: ['parsed', 'payment'],
};

describe('statusOf — уровни, число, фраза (1.1)', () => {
  it('число — пункты вне «К сведению»; худший уровень даёт тон', () => {
    const s = statusOf(input(view({
      gaps: [DISCREPANCY, { kind: 'nomenclature', label: '2 строки без позиции', lines: [4, 5] }],
      duplicates: [{ id: 'd1', number: 'СЧ-104', issuedOn: '2026-09-03', total: 128400.5 }],
      payment: { refusal: 'сумма строк не сходится', refusalKind: 'discrepancy' },
    }), { hiddenByFilter: true }));

    expect(s.count).toBe(s.items.filter(i => i.level !== 'info').length);
    expect(s.count).toBe(3);
    expect(s.items.some(i => i.level === 'info')).toBe(true);
    expect(s.mood).toBe('gate');
    expect(s.tone).toBe('warning');
    expect(s.phrase).toBe('3 замечания');
  });

  it('пункты — по уровню, внутри уровня — по месту формы', () => {
    const s = statusOf(input(view({
      gaps: [
        { kind: 'allocation', label: 'разноска' },
        { kind: 'nomenclature', label: '1 строка без позиции', lines: [3] },
        { kind: 'field', label: 'плательщик', field: 'Плательщик' },
      ],
    }), { orgsFailed: 'сервер отказал' }));
    expect(s.items.map(i => i.key)).toEqual(['orgs', 'gap:field:Плательщик', 'gap:nomenclature:1', 'gap:allocation:0']);
  });

  it('разобранный (`readiness == null`) — ни пунктов готовности, ни «Готов»', () => {
    const s = statusOf(input(view({ gaps: null, state: 'Разобран' })));
    expect(s.items).toEqual([]);
    expect(s.phrase).toBe('');
    expect(s.mood).toBe('none');
  });

  it('черновик с пустым перечнем — «Готов к «Разобран»», тон успеха', () => {
    const s = statusOf(input(view({ gaps: [] })));
    expect(s.phrase).toBe('Готов к «Разобран»');
    expect(s.tone).toBe('success');
  });

  it('только «К сведению» — числа нет, тон тихий', () => {
    const s = statusOf(input(view({ gaps: null, state: 'Разобран' }), {
      matches: { hasSupplier: true, waiting: 6, ready: 6, blocked: 0, failed: null },
    }));
    expect(s.count).toBe(0);
    expect(s.phrase).toBe('К сведению');
    expect(s.tone).toBe('neutral');
  });

  it('сбой распознавания — «Мешает работе», тон ошибки, фраза — имя пункта', () => {
    const s = statusOf(input(view(), {}, failed('Unavailable')));
    expect(s.items[0]).toMatchObject({ key: 'recognition-failed', level: 'blocking' });
    expect(s.tone).toBe('error');
    expect(s.phrase).toBe('Не распознан');
  });

  it('«не настроено» — тихо: это установка, а не счёт', () => {
    const s = statusOf(input(view(), {}, failed('NotConfigured')));
    expect(s.items.find(i => i.key === 'recognition-failed')?.level).toBe('info');
  });

  it('идёт распознавание — фраза с долей, пока нет замечаний', () => {
    const s = statusOf(input(view(), {}, { ...failed(''), state: 'running', progress: '40 %' } as InvoiceRecognition));
    expect(s.phrase).toBe('Распознаётся… 40 %');
    expect(s.mood).toBe('running');
  });

  it('просрочка — единственное замечание называет себя', () => {
    const s = statusOf(input(view({ gaps: null, state: 'Разобран', due: { manual: false, byRule: null, daysLeft: -35, overdue: true } })));
    expect(s.items[0]).toMatchObject({ key: 'overdue', level: 'check' });
    expect(s.items[0].text).toBe('Срок оплаты 03.10.2026 прошёл — просрочен на 35 дн.');
    expect(s.phrase).toBe('Просрочен на 35 дн.');
  });

  it('изменили, пока вы правили — кто и когда, «Перечитать счёт»', () => {
    const s = statusOf(input(view({ updatedBy: { via: 'user', name: 'Петрова А.', mine: false } }), {
      stale: { header: true, lines: true, correction: false },
    }));
    const item = s.items[0];
    expect(item).toMatchObject({ key: 'stale', level: 'blocking', bannerTone: 'warning' });
    expect(item.text).toContain('Петрова А.');
    expect(item.detail).toContain('Поля счёта, строки');
    expect(item.actions[0]).toMatchObject({ kind: 'reread', label: 'Перечитать счёт' });
  });
});

describe('statusOf — одно расхождение (1.2)', () => {
  it('пункт `total` с `blocks` и отказ оплаты к нему — один пункт «мешает: Разобран · оплата»', () => {
    const s = statusOf(input(view({
      gaps: [DISCREPANCY],
      payment: { refusal: 'сумма строк 90 000,00 не сходится с суммой к оплате', refusalKind: 'discrepancy' },
    })));
    const named = s.items.filter(i => /расход|не сходится/.test(i.text));
    expect(named).toHaveLength(1);
    expect(blocksText(named[0].blocks!)).toBe('мешает: Разобран · оплата');
    expect(s.items.some(i => i.key === 'pay-refusal')).toBe(false);
  });

  it('отказ «нет суммы», не покрытый пунктом готовности, — своим пунктом (change 01 не слит)', () => {
    const s = statusOf(input(view({ gaps: [], payment: { refusal: 'не указана сумма к оплате', refusalKind: 'amount' } })));
    const pay = s.items.find(i => i.key === 'pay-refusal');
    expect(pay).toMatchObject({ level: 'gate', blocks: ['pay'] });
    expect(pay?.actions[0]).toMatchObject({ kind: 'goto', place: { place: 'field', key: 'Итого' } });
  });

  it('без права оплаты отказ оплаты пунктом не идёт — ему он ничему не мешает (ревью PR #1792)', () => {
    const v = view({ gaps: [], payment: { refusal: 'не указана сумма к оплате', refusalKind: 'amount' } });
    expect(statusOf(input(v, { can: { ...CAN, pay: false } })).items.some(i => i.key === 'pay-refusal')).toBe(false);
  });

  it('расхождение, о котором готовность молчит (разобран и оплачен), — пунктом «Проверьте» (ревью PR #1792)', () => {
    const allocation = {
      allocated: false, unbalanced: [], lost: 0, discrepancy: -38400.5, tolerance: 1, withinTolerance: false, reconciled: true,
      document: { parts: [] }, stamp: 's',
    } as unknown as InvoiceView['allocation'];
    const lines = [{ ordinal: 1, allocation: { parts: [] } }] as never;
    const paid = view({ gaps: null, state: 'Разобран', allocation, lines, payment: { paid: true } });
    const item = statusOf(input(paid)).items.find(i => i.key === 'discrepancy');
    expect(item).toMatchObject({ level: 'check' });
    expect(item!.text).toMatch(/^Сумма строк расходится с суммой к оплате на 38\s400,50\s₽ — больше допуска 1,00\s₽$/);

    // Назвал пункт готовности — второй раз не называется.
    const draft = statusOf(input(view({ gaps: [DISCREPANCY], allocation, lines })));
    expect(draft.items.filter(i => /расход/.test(i.text))).toHaveLength(1);
  });

  it('нулевая сумма при строках — один пункт у «Итого», расхождения рядом нет (costs-invoice-screen-01, D5)', () => {
    // Сервер: пункт поля «Итого», расхождение не сверено (`reconciled: false`), а число в сводке осталось.
    const allocation = {
      allocated: false, unbalanced: [], lost: 0, discrepancy: -100, tolerance: 1, withinTolerance: false, reconciled: false,
      document: { parts: [] }, stamp: 's',
    } as unknown as InvoiceView['allocation'];
    const lines = [{ ordinal: 1, allocation: { parts: [] } }] as never;
    const gap: InvoiceReadinessGap = { kind: 'field', label: 'сумма к оплате', field: 'Итого', blocks: ['parsed', 'payment'] };

    const draft = statusOf(input(view({
      gaps: [gap], allocation, lines, payment: { refusal: 'не указана сумма к оплате', refusalKind: 'amount' },
    })));
    expect(draft.items.map(i => i.key)).toEqual(['gap:field:Итого']);

    // Разобранный без суммы до обновления (Э3): перечня нет, отказ оплаты — свой пункт, расхождения тоже нет.
    const parsed = statusOf(input(view({
      gaps: null, state: 'Разобран', allocation, lines, payment: { refusal: 'не указана сумма к оплате', refusalKind: 'amount' },
    })));
    expect(parsed.items.map(i => i.key)).toEqual(['pay-refusal']);
  });

  it('отклонённому отказ оплаты пунктом не идёт', () => {
    const s = statusOf(input(view({ gaps: null, state: 'Отклонён', payment: { refusal: 'счёт отклонён', refusalKind: 'rejected' } })));
    expect(s.items).toEqual([]);
  });
});

describe('statusOf — отказ, а не пропуск (1.3)', () => {
  it('отказ подсказок — «запомненное не проверено» с «Повторить»', () => {
    const s = statusOf(input(view(), { matches: { hasSupplier: true, waiting: 3, ready: 0, blocked: 0, failed: 'сервер отказал' } }));
    const item = s.items.find(i => i.key === 'matches-failed')!;
    expect(item.text).toBe('Запомненное не проверено: сервер отказал');
    expect(item.actions[0]).toMatchObject({ kind: 'retry-matches', label: 'Повторить' });
  });

  it('поставщик изменён и не сохранён — запомненное прежнего не предлагается, причина названа (#1657)', () => {
    const s = statusOf(input(view(), {
      matches: { hasSupplier: true, supplierPending: true, waiting: 3, ready: 0, blocked: 0, failed: 'сервер отказал' },
    }));
    expect(s.items.map(i => i.key).filter(k => k.startsWith('matches-'))).toEqual(['matches-pending']);
    const item = s.items.find(i => i.key === 'matches-pending')!;
    expect(item.text).toMatch(/Поставщик изменён и не сохранён/);
    expect(item.detail).toMatch(/прежнего поставщика не подставляется/);
    expect(item.actions[0]).toMatchObject({ kind: 'goto', place: { place: 'field', key: 'Поставщик' } });
  });

  it('неизвестный вид пункта готовности — пункт со словами сервера', () => {
    const s = statusOf(input(view({ gaps: [{ kind: 'something-new', label: 'новое условие сервера' }] })));
    expect(s.items[0]).toMatchObject({ level: 'gate', text: 'Новое условие сервера' });
  });

  it('состояние распознавания не пришло — «Проверьте», «Повторить», а не «не распознан»', () => {
    const s = statusOf(input(view(), {}, undefined, true));
    expect(s.items[0]).toMatchObject({ key: 'recognition-unread', level: 'check' });
    expect(s.items[0].actions[0].kind).toBe('retry-recognition');
  });

  it('схема типа не прочитана — «Проверьте», «Повторить»', () => {
    const s = statusOf(input(view(), { extrasFailed: 'сервер отказал' }));
    expect(s.items[0]).toMatchObject({ key: 'extras', level: 'check' });
  });
});

describe('statusOf — места и права (1.4)', () => {
  const DUP = { duplicates: [{ id: 'd1', number: 'СЧ-104', issuedOn: '2026-09-03', total: 1 }] };

  it('«Похоже на дубликат» без права удалять — без «Это дубль…», «Открыть» остаётся', () => {
    const s = statusOf(input(view(DUP), { can: { ...CAN, edit: false, delete: false } }));
    const dup = s.items.find(i => i.key === 'duplicate')!;
    expect(dup.actions.map(a => a.label)).toEqual(['Открыть СЧ-104']);
  });

  it('с правом — «Это дубль — удалить…» действием пункта, «Открыть» — вторым (строкой перечня)', () => {
    const s = statusOf(input(view(DUP)));
    expect(s.items.find(i => i.key === 'duplicate')!.actions.map(a => a.kind)).toEqual(['delete', 'open']);
  });

  it('пункт строк ведёт к первой строке; без права — виден, не ведёт', () => {
    const gaps: InvoiceReadinessGap[] = [{ kind: 'nomenclature', label: '2 строки без позиции', lines: [4, 5] }];
    const ok = statusOf(input(view({ gaps, lines: Array.from({ length: 6 }, (_, i) => ({ ordinal: i + 1, allocation: { parts: [] } })) as never })));
    expect(ok.items[0].actions[0]).toMatchObject({ kind: 'goto', label: 'К строкам', place: { place: 'lines', line: 4 } });

    const no = statusOf(input(view({ gaps }), { can: { ...CAN, edit: false } }));
    expect(no.items.find(i => i.key.startsWith('gap'))!.actions).toEqual([]);
    expect(no.items.find(i => i.key.startsWith('gap'))!.why).toContain('право вводить');
  });

  it('метки со скана — «Всё верно (N)» только с правом и у незапертого', () => {
    const s = statusOf(input(view(), { marks: ['Номер', 'Итого'] }));
    expect(s.items[0].text).toBe('2 значения со скана не проверены: «Номер», «Сумма к оплате»');
    expect(s.items[0].actions[0]).toMatchObject({ kind: 'confirm-marks', label: 'Всё верно (2)' });

    const ro = statusOf(input(view(), { marks: ['Номер'], can: { ...CAN, edit: false } }));
    expect(ro.items.find(i => i.key === 'marks')!.actions).toEqual([]);
  });

  it('метки у полей вне формы — «Проверьте» со своим «Всё верно»; без права — без действия (46)', () => {
    const s = statusOf(input(view(), { hiddenMarks: ['КодПроекта'], markTitles: { КодПроекта: 'Код проекта' } }));
    const item = s.items.find(i => i.key === 'marks-hidden')!;
    expect(item).toMatchObject({ level: 'check', actions: [{ kind: 'confirm-hidden-marks', label: 'Всё верно (1)' }] });
    expect(item.text).toBe('1 значение со скана не проверено у полей, которых форма не показывает: «Код проекта»');
    expect(s.count).toBe(1);
    const ro = statusOf(input(view(), { hiddenMarks: ['КодПроекта'], can: { ...CAN, edit: false } }));
    expect(ro.items.find(i => i.key === 'marks-hidden')!.actions).toEqual([]);
  });

  it('«Показать на скане» — у пунктов о метках, после «Всё верно» и при файле; без права остаётся только оно (D-11.4, вариант А)', () => {
    const show = { kind: 'show-scan', label: 'Показать на скане' };
    const s = statusOf(input(view(), { marks: ['Номер'], hiddenMarks: ['КодПроекта'], canShowScan: true }));
    expect(s.items.find(i => i.key === 'marks')!.actions).toMatchObject([{ kind: 'confirm-marks' }, show]);
    expect(s.items.find(i => i.key === 'marks-hidden')!.actions).toMatchObject([{ kind: 'confirm-hidden-marks' }, show]);

    const ro = statusOf(input(view(), { marks: ['Номер'], canShowScan: true, can: { ...CAN, edit: false } }));
    expect(ro.items.find(i => i.key === 'marks')!.actions).toEqual([show]);

    const noFile = statusOf(input(view(), { marks: ['Номер'] }));
    expect(noFile.items.find(i => i.key === 'marks')!.actions.map(a => a.kind)).toEqual(['confirm-marks']);
  });

  it('отгрузка раньше даты счёта — «Проверьте», «К полю» даты отгрузки; по набранному в форме (32)', () => {
    const shipped = (v: InvoiceView, form: Partial<StatusInput['form']> = {}) =>
      statusOf(input(v, { shippedEarly: true, ...form })).items.find(i => i.key === 'shipped-early');
    expect(shipped(view())).toMatchObject({
      level: 'check', text: 'Отгрузка раньше даты счёта',
      actions: [{ kind: 'goto', place: { place: 'field', key: 'ДатаОтгрузки' } }],
    });
    expect(statusOf(input(view())).items.some(i => i.key === 'shipped-early')).toBe(false);
    // Только тому, кто поправит, и пока срок что-то значит (ревью PR #1794).
    expect(shipped(view(), { can: { ...CAN, edit: false } })).toBeUndefined();
    expect(shipped(view({ payment: { paid: true } }))).toBeUndefined();
    expect(shipped(view({ state: 'Отклонён' }))).toBeUndefined();
    expect(shipped(view({ payment: { lockedBy: 'период закрыт' } }))).toBeUndefined();
    expect(shipped(view({ payment: { lockedBy: 'период закрыт' } }), { correcting: true })).toBeDefined();
  });

  it('режим НДС со скана — «К сведению» с числами для сверки, ведёт к строкам; в исправлении молчит (36)', () => {
    const v = view({
      unconfirmed: ['vatMode'], vatMode: 'OnTop', requisites: { Состояние: 'Черновик', Итого: 1830, ВТомЧислеНДС: 330 },
      totals: { count: 1, amount: 1500, vat: 330, vatMissing: 0, net: 1500, withoutNomenclature: 0 },
    } as ViewPatch);
    const s = statusOf(input(v));
    const item = s.items.find(i => i.key === 'vat-mode')!;
    expect(item).toMatchObject({
      level: 'info', text: 'Режим НДС со скана не проверен: цены без НДС, НДС сверху',
      actions: [{ kind: 'goto', place: { place: 'lines' } }],
    });
    expect(item.detail).toMatch(/^строки 1\s500,00\s₽ \+ НДС 330,00\s₽ = к оплате 1\s830,00\s₽$/);
    expect(s.count).toBe(0);
    expect(statusOf(input(v, { correcting: true })).items.some(i => i.key === 'vat-mode')).toBe(false);
    // Режим переключён и не сохранён: у переключателя ни рамки, ни «Верно» — пункт о сохранённом молчит (ревью PR #1794).
    expect(statusOf(input(v, { vatModeChanged: true })).items.some(i => i.key === 'vat-mode')).toBe(false);
    // Ведёт туда, где «Верно» есть: заперт или права нет — пункта нет (ревью PR #1794).
    expect(statusOf(input(v, { can: { ...CAN, edit: false } })).items.some(i => i.key === 'vat-mode')).toBe(false);
    const locked = { ...v, payment: { ...v.payment, lockedBy: 'период закрыт' } } as InvoiceView;
    expect(statusOf(input(locked)).items.some(i => i.key === 'vat-mode')).toBe(false);
  });

  it('метки: глагол — по числительному; подпись дописанного поля — из схемы', () => {
    const many = Array.from({ length: 21 }, (_, i) => `Поле${i}`);
    expect(statusOf(input(view(), { marks: many })).items[0].text).toMatch(/^21 значение со скана не проверено: /);
    const s = statusOf(input(view(), { marks: ['КодПроекта'], markTitles: { КодПроекта: 'Код проекта' } }));
    expect(s.items[0].text).toBe('1 значение со скана не проверено: «Код проекта»');
  });

  it('замечания распознавания — только тому, кто правит: без права их на экране нет (ревью PR #1792)', () => {
    const done = { ...failed('x'), state: 'done', reason: null, notes: ['поставщик прочитан неуверенно'] } as InvoiceRecognition;
    expect(statusOf(input(view(), {}, done)).items.find(i => i.key === 'recognition-notes')!.actions[0])
      .toMatchObject({ kind: 'goto', place: { place: 'part', part: 'who' } });
    expect(statusOf(input(view(), { can: { ...CAN, edit: false } }, done)).items.some(i => i.key === 'recognition-notes')).toBe(false);
  });

  it('заперт — «К сведению», путь к «Учётному периоду» только с правом закрывать', () => {
    const locked = view({ gaps: null, state: 'Разобран', payment: { lockedBy: 'период 09.2026 закрыт' } });
    expect(statusOf(input(locked)).items.find(i => i.key === 'locked')!.actions).toEqual([]);
    expect(statusOf(input(locked, { can: { ...CAN, closePeriod: true } })).items.find(i => i.key === 'locked')!.actions[0].kind).toBe('periods');
  });
});

describe('banner и подсказка', () => {
  it('сбой распознавания и удалённые записи — один banner, «и ещё 1»', () => {
    const s = statusOf(input(view({ references: { supplier: 'lost', payer: 'present', documentType: 'present' } }), {}, failed('NoAnswer')));
    const banner = bannerOf(s)!;
    expect(banner.item.key).toBe('recognition-failed');
    expect(banner.rest).toBe(1);
    expect(banner.item.actions.map(a => a.kind)).toEqual(['recognize', 'delete']);
  });

  it('только дубликат — banner нет', () => {
    const s = statusOf(input(view({ duplicates: [{ id: 'd1', number: '1', issuedOn: null, total: null }] })));
    expect(bannerOf(s)).toBeNull();
  });

  it('кратко — фраза и первые пункты', () => {
    const s = statusOf(input(view({ gaps: [DISCREPANCY, { kind: 'lines', label: 'строки' }, { kind: 'allocation', label: 'разноска' }] })));
    expect(briefOf(s)).toMatch(/^3 замечания — .+; Строк нет и ещё 1\. Щёлкните — весь перечень$/);
  });
});

describe('удалённые записи у счёта закрытого периода (E2, #1100; ревью PR #1392)', () => {
  const LOST = view({ references: { supplier: 'lost', payer: 'present', documentType: 'present' } });
  const lost = (form: Partial<StatusInput['form']>, v: InvoiceView = LOST) => statusOf(input(v, form)).items.find(i => i.key === 'lost')!;
  const closed = view({ gaps: null, state: 'Разобран', references: LOST.references, payment: { lockedBy: 'период закрыт' } });

  it('путь «Исправить» назван только тому, у кого он есть', () => {
    expect(lost({ can: { ...CAN, correctable: true } }, closed).detail).toContain('заменяют исправлением с причиной');
    expect(lost({}, closed).detail).not.toContain('исправлением');
    expect(lost({}, closed).detail).toContain('здесь это не исправить');
    expect(lost({}, closed).level).toBe('info');
  });

  it('открытый счёт и исправление — «Мешает работе», выбрать другую запись, ведёт к полю', () => {
    const open = lost({});
    expect(open.level).toBe('blocking');
    expect(open.detail).toContain('выбрать в поле другую запись');
    expect(open.actions[0]).toMatchObject({ kind: 'goto', place: { place: 'field', key: 'Поставщик' } });
    expect(lost({ correcting: true }, closed).level).toBe('blocking');
    expect(lost({ gone: true }).detail).toContain('Счёт удалён');
  });

  it('без права правки — «К сведению», не ведёт и не встаёт banner (ревью PR #1792)', () => {
    const ro = lost({ can: { ...CAN, edit: false } });
    expect(ro.level).toBe('info');
    expect(ro.actions).toEqual([]);
    expect(ro.detail).toContain('право вводить счета');
    expect(bannerOf(statusOf(input(LOST, { can: { ...CAN, edit: false } })))).toBeNull();
  });
});

describe('удаление нового черновика из перечня — без многоточия (costs-invoice-screen-13-undo, D-13.5)', () => {
  const deletes = (now: boolean) => statusOf(input(view(), { can: { ...CAN, deleteNow: now } }, failed('NoAnswer')))
    .items.flatMap(i => i.actions).filter(a => a.kind === 'delete').map(a => a.label);

  it('свежий черновик: «Не тот файл — удалить черновик»; иначе — с многоточием, окном', () => {
    expect(deletes(true)).toEqual(['Не тот файл — удалить черновик']);
    expect(deletes(false)).toEqual(['Не тот файл — удалить черновик…']);
  });
});
