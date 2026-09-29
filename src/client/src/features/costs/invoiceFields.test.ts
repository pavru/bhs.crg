import { describe, it, expect } from 'vitest';
import {
  BLOCKS, K, READ_ONLY_KEYS, SCAN_BESIDE_MIN_WIDTH,
  asInput, catalogRef, duplicateLabel, formatMoney, fromInput, refEntryId,
  isMarked, moneyInput, scanFitsBeside, toRequisites, unconfirmedInBlock, unconfirmedOutsideBlocks,
} from './invoiceFields';

describe('сборка реквизитов на отправку', () => {
  it('поле, которого нет в форме, ПЕРЕЖИВАЕТ сохранение', () => {
    // Тип счёта расширяемый: заказчик дописал «ЦентрЗатрат», формы у поля нет. Поимённая сборка
    // стирала бы его молча — ни сервер, ни охрана записи отсутствие поля ошибкой не считают.
    const stored = { [K.number]: 'СЧ-1', ЦентрЗатрат: 'Склад №2' };

    expect(toRequisites(stored, { [K.number]: 'СЧ-2' }))
      .toEqual({ [K.number]: 'СЧ-2', ЦентрЗатрат: 'Склад №2' });
  });

  it('поля, которые ведёт код, уезжают неизменными', () => {
    // Вырезав их, клиент получил бы отказ: охрана записи ядра читает отсутствующее запертое поле как
    // стёртое — «верните его в запись как есть».
    const stored = { [K.state]: 'Черновик', [K.payment]: 'Не оплачен', [K.scan]: null };

    const sent = toRequisites(stored, { [K.number]: 'СЧ-3' });

    for (const key of READ_ONLY_KEYS) expect(sent).toHaveProperty(key);
    expect(sent[K.state]).toBe('Черновик');
  });

  it('правка перекрывает лежащее значение, а не дописывается рядом', () => {
    expect(toRequisites({ [K.total]: 100 }, { [K.total]: 200 })).toEqual({ [K.total]: 200 });
  });

  it('стёртое поле уезжает как null, а не как пустая строка', () => {
    // Пустую строку сервер прочтёт как «значение есть, оно пустое», а человек имел в виду «номера нет».
    expect(fromInput('   ')).toBeNull();
    expect(fromInput('  СЧ-7 ')).toBe('СЧ-7');
  });
});

describe('блоки формы', () => {
  it('шапка — первая и в ней то, что названо в ТЗ', () => {
    expect(BLOCKS[0].fields).toEqual([K.supplier, K.number, K.date, K.total]);
  });

  it('каждое поле стоит ровно в одном блоке', () => {
    const all = BLOCKS.flatMap(b => [...b.fields]);
    expect(all.length).toBe(new Set(all).size);
  });

  it('«Всё верно» снимает метки ТОЛЬКО своего блока', () => {
    const unconfirmed = [K.supplier, K.number, K.dueDate];

    expect(unconfirmedInBlock(BLOCKS[0], unconfirmed)).toEqual([K.supplier, K.number]);
    expect(unconfirmedInBlock(BLOCKS[2], unconfirmed)).toEqual([K.dueDate]);
  });

  it('в блоке без меток перечень пуст — кнопке там не место', () => {
    // Пустой перечень сервер отвергает: предлагать действие, которое заведомо откажет, — обман.
    expect(unconfirmedInBlock(BLOCKS[1], [K.number])).toEqual([]);
  });

  it('правка снимает метку сразу, не дожидаясь сохранения', () => {
    // Метка на поле, которое человек только что переписал, утверждала бы о нём неправду.
    expect(isMarked(K.number, [K.number], {})).toBe(true);
    expect(isMarked(K.number, [K.number], { [K.number]: 'СЧ-9' })).toBe(false);
    expect(isMarked(K.number, [], {})).toBe(false);
  });

  it('метка на дописанном заказчиком поле не теряется', () => {
    // Иначе метка была бы у поля, которое ни один блок не подтверждает: снять её стало бы нечем.
    expect(unconfirmedOutsideBlocks([K.number, 'ЦентрЗатрат'])).toEqual(['ЦентрЗатрат']);
  });
});

describe('скан рядом с формой', () => {
  it('на 1280 — рядом, на 1279 — нет', () => {
    expect(scanFitsBeside(SCAN_BESIDE_MIN_WIDTH)).toBe(true);
    expect(scanFitsBeside(SCAN_BESIDE_MIN_WIDTH - 1)).toBe(false);
  });
});

describe('ссылка на запись справочника', () => {
  it('собирается и читается той же формой, что хранит ядро', () => {
    expect(refEntryId(catalogRef('9f1f0f1e-0000-4000-8000-000000000001')))
      .toBe('9f1f0f1e-0000-4000-8000-000000000001');
  });

  it('чужое значение ссылкой не считается', () => {
    expect(refEntryId(null)).toBeNull();
    expect(refEntryId('ООО «Кабель-Торг»')).toBeNull();
    expect(refEntryId({ $ref: 'document', instanceId: 'x' })).toBeNull();
  });
});

describe('показ значений', () => {
  it('пустое значение — пустая строка, а не «null»', () => {
    expect(asInput(null)).toBe('');
    expect(asInput(undefined)).toBe('');
    expect(asInput(0)).toBe('0');
  });

  it('сумма в поле ввода — с запятой и без разделения разрядов', () => {
    // Пробел внутри правимого значения заставляет думать, одно это число или два.
    expect(moneyInput(128400.5)).toBe('128400,50');
    expect(moneyInput(null)).toBe('');
    // Текст человека не переписываем: он набирает, курсор должен стоять на месте.
    expect(moneyInput('128 400,5')).toBe('128 400,5');
  });

  it('дубликат назван тем, по чему человек его узнает', () => {
    // \u00A0 — неразрывный пробел: именно им `ru-RU` разделяет разряды. Написав здесь обычный, мы
    // получили бы падение с ОДИНАКОВЫМИ на вид строками, и разницу пришлось бы искать глазами.
    expect(duplicateLabel({ id: 'x', number: 'СЧ-7', issuedOn: '2026-09-03', total: 1200 }))
      .toBe('СЧ-7 от 03.09.2026 на 1\u00A0200,00 ₽');
  });

  it('дубликат без номера назван, а не пропущен', () => {
    expect(duplicateLabel({ id: 'x', number: null, issuedOn: null, total: null }))
      .toBe('без номера');
  });

  it('сумма — с двумя знаками и рублём', () => {
    expect(formatMoney(1234.5)).toBe('1\u00A0234,50 ₽');
  });
});
