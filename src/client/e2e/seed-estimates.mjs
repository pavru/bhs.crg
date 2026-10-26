// Посев смет модуля «Планирование» (change `plan-09-estimate-ui`, задача 4.4).
//
// Отдельным файлом от `seed.mjs` — по храповику размера, как и посев счетов. Помощники приходят
// параметрами: они держат состояние `seed.mjs` (токен, адрес).
//
// ⚠️ ФАЙЛ СМЕТЫ СОБИРАЕТСЯ ЗДЕСЬ, из выдуманных строк, а не берётся из образцов ГРАНД-Сметы в
// `TestData/GrandSmeta`: те — файлы заказчика, и класть их на стенд прогонов нельзя ни в каком виде.
// Собранный файл — голый `Data.xml` (разбор принимает и его, и `.gsfx`) по схеме ЛСР: два раздела,
// работы в «100 шт» и «100 м», работа с ресурсом-материалом и строка-материал (ФССЦ). Итоги позиций
// записаны, как их пишет программа: досчёт итогов — забота разбора, а не экранов смет.

/** Единицы измерения: справочник заводит человек; классификатор видов работ появится со следующим запуском. */
const UNIT_TYPE = 'ЕдиницаИзмерения';
export const ESTIMATE_NAME = 'ЭОМ корпус 2 (посев)';

const position = ({ number, code, caption, units, quantity, total, material }, sysId) => `
      <Position SysID="${sysId}" PriceLevel="Curr" Number="${number}" Code="${code}" Caption="${caption}" Units="${units}">
        <Quantity Fx="${quantity}" Precision="7" Result="${quantity}" />${material ? `
        <Resources>
          <Mat Code="${material.code}" Caption="${material.caption}" Units="${material.units}" Quantity="${material.quantity}" />
        </Resources>` : ''}
        <Itog>
          <ItogBim>
            <Itog DataType="TZ" Total="1,5" />
            <Itog DataType="TotalWithNP" TotalBase="${total}" TotalCurr="${total}" />
          </ItogBim>
        </Itog>
      </Position>`;

/** Строки сметы: раздел 1 — розетки и выключатели (100 шт), гофра с материалом (100 м), провод (ФССЦ). */
const CHAPTERS = [
  ['Групповые сети (посев)', [
    { number: '1', code: 'ГЭСНм08-03-591-02', caption: 'Розетка штепсельная утопленного типа при скрытой проводке', units: '100 шт', quantity: '2,16', total: '94521,6' },
    { number: '2', code: 'ГЭСНм08-03-591-08', caption: 'Выключатель одноклавишный утопленного типа', units: '100 шт', quantity: '1,44', total: '51408' },
    { number: '3', code: 'ГЭСНм08-02-409-01', caption: 'Труба гофрированная ПВХ по стенам и колоннам', units: '100 м', quantity: '6,3', total: '58212',
      material: { code: 'ФССЦ-21.1.01.01-0001', caption: 'Труба гофрированная ПВХ d20 (посев)', units: 'м', quantity: '100' } },
    { number: '4', code: 'ФССЦ-21.2.01.02-0071', caption: 'Провод ПуГВ 1х2,5 (посев)', units: 'м', quantity: '4820', total: '149420' },
  ]],
  ['Электроосвещение (посев)', [
    { number: '5', code: 'ГЭСНм08-03-593-06', caption: 'Светильник потолочный с лампами', units: '100 шт', quantity: '0,4', total: '18200' },
  ]],
];

/** Файл сметы. `extra` — вторая редакция: та же смета и ещё одна строка в конце. */
export function estimateXml(extra = false) {
  // SysID — сквозной номер элемента документа: схема требует его у разделов и позиций.
  let sysId = 0;
  const chapters = CHAPTERS.map(([caption, rows], i) => {
    const all = extra && i === CHAPTERS.length - 1
      ? [...rows, { number: '6', code: 'ГЭСНм08-03-593-06', caption: 'Светильник настенный', units: '100 шт', quantity: '0,12', total: '5460' }]
      : rows;
    const chapter = ++sysId;
    return `
    <Chapter SysID="${chapter}" Caption="${caption}">${all.map(row => position(row, ++sysId)).join('')}
    </Chapter>`;
  }).join('');
  return `<?xml version="1.0" encoding="utf-8"?>
<Document Generator="GrandSmeta" ProgramVersion="16.2.2.59452" DocumentType="{2B0470FD-477C-4359-9F34-EEBE36B7D340}">
  <Properties LocNum="02-01-01" Description="ЭОМ корпус 2 (посев)" />
  <Chapters>${chapters}
  </Chapters>
</Document>
`;
}

/**
 * Пользователи, справочник единиц и смета с двумя редакциями. Возвращает идентификатор сметы либо `null`,
 * если модуля `plan` в приложении нет, — и говорит об этом вслух: прогон смет на таком стенде отказывает.
 */
export async function seedEstimates({ api, findType, ensureUser, apiBase, token, constructionId }) {
  const existing = await api('GET', `/plan/estimates?constructionId=${constructionId}`).catch(e => {
    console.log(`  ! сметы не сеются: модуль plan недоступен (${e.message})`);
    return null;
  });
  if (existing === null) return null;

  // «Сметчик» импортирует и сопоставляет; «Менеджер проекта» читает сметы без себестоимости.
  await ensureUser(process.env.SMOKE_ESTIMATOR_EMAIL || 'smeta@bhs.local', 'Светлана Сметова', 'Estimator');
  await ensureUser(process.env.SMOKE_PM_EMAIL || 'pm@bhs.local', 'Павел Проектов', 'ProjectManager');
  await ensureUnits({ api, findType });

  let estimate = existing.items.find(e => e.name === ESTIMATE_NAME);
  if (!estimate) {
    const committed = await importFile({ api, apiBase, token, form: { constructionId, 'newEstimate.name': ESTIMATE_NAME, 'newEstimate.kind': 'main' } });
    estimate = committed.estimate;
    console.log(`  + смета «${ESTIMATE_NAME}», ред. 1`);
  }
  if (estimate.revisions.length < 2) {
    await importFile({ api, apiBase, token, form: { constructionId, estimateId: estimate.id }, extra: true });
    console.log(`  + смета «${ESTIMATE_NAME}», ред. 2`);
  }
  return estimate.id;
}

/**
 * Справочник единиц — только ЗАВЕСТИ недостающее: найденное не трогаем. В своей базе разработчика справочник
 * живой, и доводка его схемы или записей под посев переписала бы чужие данные.
 */
async function ensureUnits({ api, findType }) {
  let type = await findType(UNIT_TYPE);
  if (!type) {
    type = await api('POST', '/document-types', {
      name: 'Единица измерения (посев)', code: UNIT_TYPE, kind: 'Composite', parentId: null,
      schema: JSON.stringify({ fields: [{ key: 'Наименование', title: 'Наименование', type: 'string', required: false }] }),
      isAbstract: false, module: 'core',
    });
    console.log('  + тип «Единица измерения (посев)» — классификатор видов работ появится со следующим запуском');
  }
  const have = await api('GET', `/common-data?purpose=display&typeId=${type.id}`);
  for (const name of ['шт', 'м']) {
    if (have.some(e => e.displayName === name)) continue;
    await api('POST', '/common-data', {
      displayName: name, compositeTypeId: type.id, data: JSON.stringify({ Наименование: name }),
      scope: 'System', scopeId: null, aliases: [],
    });
    console.log(`  + единица «${name}»`);
  }
}

/** Загрузка файла, ожидание разбора и создание редакции — как это делает окно загрузки. */
async function importFile({ api, apiBase, token, form, extra = false }) {
  const body = new FormData();
  body.append('file', new Blob([estimateXml(extra)], { type: 'application/xml' }), extra ? 'ЛСР ред. 2 (посев).xml' : 'ЛСР (посев).xml');
  for (const [k, v] of Object.entries(form)) body.append(k, v);
  const res = await fetch(`${apiBase}/api/plan/imports`, { method: 'POST', headers: { Authorization: `Bearer ${token}` }, body });
  if (!res.ok) throw new Error(`POST /plan/imports → ${res.status} ${(await res.text()).slice(0, 300)}`);
  let imported = await res.json();

  // Разбор — фоновая задача: ждём исхода, а не первого ответа.
  const deadline = Date.now() + 120_000;
  while (imported.state === 'parsing') {
    if (Date.now() > deadline) throw new Error(`разбор сметы «${imported.fileName}» не закончился за две минуты`);
    await new Promise(r => setTimeout(r, 1000));
    imported = await api('GET', `/plan/imports/${imported.id}`);
  }
  if (imported.state !== 'parsed' || imported.unparsed.length > 0)
    throw new Error(`синтетическая смета разобрана не целиком (${imported.state}): `
      + `${imported.refusal?.text ?? imported.unparsed.map(u => `${u.place}: ${u.reason}`).join('; ')}`);
  return api('POST', `/plan/imports/${imported.id}/commit`, { acceptPartial: false }, { 'If-Match': imported.version });
}
