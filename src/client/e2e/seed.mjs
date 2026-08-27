// Посев синтетических данных для живых прогонов (issue #872).
//
// ЗАЧЕМ ОН ЕСТЬ. Прогоны в `e2e/` смотрят на живое приложение, и смотреть им нужно НА ЧТО-ТО:
// пустая база — пустые экраны, и половина проверок падает с «проверять нечего». На моей машине
// данные копились месяцами вручную; в CI база каждый раз новая.
//
// ПОЧЕМУ НЕ ДАМП ЖИВОЙ БАЗЫ. Репозиторий публичный. В рабочей базе — настоящие организации, живые
// люди в составах комиссий, сертификаты соответствия. Дамп сюда класть нельзя ни в каком виде,
// поэтому данные здесь синтетические и создаются через тот же REST API, которым пользуется клиент.
// Побочная выгода: посев ломается, когда ломается контракт API, — и ломается громко.
//
// ЧТО СЕЕТСЯ — ровно то, чего требуют прогоны, входящие в CI (см. `e2e/README.md`, раздел
// «Что гоняется в CI»). Не «база на все случаи»: чего не требует ни одна проверка, того здесь нет.
//
// Идемпотентность: повторный запуск ничего не задваивает — всё, что уже есть, пропускается. Это
// нужно и локально (посеять поверх своей базы, не ломая её), и в CI при перезапуске работы.
//
// Запуск:  SEED_API=http://localhost:5000 node e2e/seed.mjs
// Печатает в конце строки `SMOKE_*=...` — их работа CI кладёт в окружение прогонов.
//
// ⚠️ Имена этих строк не должны совпадать с переменными, объявленными на уровне работы в
// `ci.yml` (сегодня там `SMOKE_BASE`): переменная работы сильнее дописанной в `$GITHUB_ENV`, и
// совпавшее имя приняли бы, дописали и МОЛЧА проигнорировали.

const API = (process.env.SEED_API || 'http://localhost:5000').replace(/\/$/, '');
const ADMIN_EMAIL = process.env.SMOKE_EMAIL || 'admin@bhs.local';
const ADMIN_PASSWORD = process.env.SMOKE_PASSWORD || 'Demo12345!';
const USER_EMAIL = process.env.SMOKE_USER_EMAIL || 'petrov@bhs.local';
const USER_PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';

let token = null;

async function api(method, path, body) {
  const res = await fetch(`${API}/api${path}`, {
    method,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`${method} ${path} → ${res.status} ${text.slice(0, 300)}`);
  }
  return text ? JSON.parse(text) : null;
}

/** Ждём, пока приложение поднимется и домигрирует: в CI посев стартует сразу за запуском. */
async function waitForApi(timeoutMs = 180_000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      const v = await fetch(`${API}/api/version`);
      if (v.ok) return (await v.json()).version;
    } catch { /* ещё не слушает */ }
    if (Date.now() > deadline) throw new Error(`API не поднялся за ${timeoutMs / 1000} с: ${API}`);
    await new Promise(r => setTimeout(r, 2000));
  }
}

async function login(email, password) {
  const r = await api('POST', '/auth/login', { email, password });
  return r.accessToken;
}

/**
 * Первая учётная запись заводится ТОЛЬКО через открытую регистрацию (`/auth/register` закрывается,
 * едва появляется первый пользователь) и получает роль Admin. Если база уже посеяна — просто входим.
 */
async function ensureAdmin() {
  const { open } = await api('GET', '/auth/registration-open');
  if (open) {
    await api('POST', '/auth/register', {
      email: ADMIN_EMAIL, password: ADMIN_PASSWORD, displayName: 'Администратор прогонов',
    });
    console.log(`  + администратор ${ADMIN_EMAIL}`);
  }
  token = await login(ADMIN_EMAIL, ADMIN_PASSWORD);
}

/** Не-администратор нужен ровно одной проверке: раздел настроек обязан его не пускать. */
async function ensureUser() {
  const users = await api('GET', '/users');
  const list = Array.isArray(users) ? users : users.items ?? [];
  if (list.some(u => (u.email ?? '').toLowerCase() === USER_EMAIL.toLowerCase())) return;
  await api('POST', '/users', {
    email: USER_EMAIL, displayName: 'Пётр Петров', password: USER_PASSWORD, role: 'User',
  });
  console.log(`  + пользователь ${USER_EMAIL}`);
}

// Сверять здесь нечего: стройка ищется по имени, а имя — единственное её поле. Как только у
// посева появится стройка с чем-то ещё, этот случай станет таким же, как у типов ниже.
async function ensureConstruction() {
  const all = await api('GET', '/constructions');
  const found = all.find(c => c.name === 'Демо-стройка');
  if (found) return found.id;
  const created = await api('POST', '/constructions', { name: 'Демо-стройка' });
  console.log('  + стройка «Демо-стройка»');
  return created.id;
}

/**
 * Тип поля из реестра. Нужен двум проверкам сразу: сводка поля обязана показать его ИМЯ
 * («Цело число»), а пикер типа — раздел «Типы полей (реестр)». Имя намеренно повторяет то,
 * что ищет `types-smoke`.
 */
async function ensurePrimitiveType() {
  const body = {
    name: 'Цело число', code: 'INT_SEED', baseType: 'number',
    description: 'Целое число без дробной части',
    constraints: JSON.stringify({ integer: true }),
    allowedTags: null,
  };
  const found = (await api('GET', '/primitive-types')).find(t => t.code === 'INT_SEED');
  if (found) {
    await api('PUT', `/primitive-types/${found.id}`, body);
    return found.id;
  }
  const created = await api('POST', '/primitive-types', body);
  console.log('  + тип поля «Цело число»');
  return created.id;
}

/**
 * Перечисление ДЛИННЕЕ трёх вариантов — это условие проверки превью: она считает хвост «(+N−3)»
 * и на коротком списке осталась бы зелёной при сломанном превью, ничего не проверив.
 */
async function ensureEnumType() {
  // Варианты — пары {code,label}: превью в списке собирается ИМЕННО из `label`, и на массиве голых
  // строк оно выходит пустым (проверено — проверка краснела на «нет хвоста (+2)»).
  const body = {
    name: 'Стадия работ', code: 'STAGE_SEED', description: 'Стадия выполнения',
    values: JSON.stringify([
      { code: 'PREP', label: 'Подготовка' },
      { code: 'MOUNT', label: 'Монтаж' },
      { code: 'TEST', label: 'Испытания' },
      { code: 'HANDOVER', label: 'Сдача' },
      { code: 'WARRANTY', label: 'Гарантия' },
    ]),
  };
  const found = (await api('GET', '/enum-types')).find(t => t.code === 'STAGE_SEED');
  if (found) {
    // Перезаписываем, а не пропускаем: база могла быть посеяна прежней редакцией скрипта, и
    // «код на месте — значит всё хорошо» оставило бы в ней негодные варианты.
    await api('PUT', `/enum-types/${found.id}`, body);
    return found.id;
  }
  const created = await api('POST', '/enum-types', body);
  console.log('  + перечисление «Стадия работ» (5 вариантов)');
  return created.id;
}

async function findType(code) {
  const all = await api('GET', '/document-types');
  return all.find(t => t.code === code) ?? null;
}

/**
 * Заводит тип или ДОВОДИТ найденный до нужного состояния — схему и группу пишем всегда.
 *
 * «Код на месте — значит всё на месте» здесь неверно, и это не теория: создание типа и простановка
 * группы — два запроса. Упади второй (сеть, 5xx, обрыв), и всякий следующий запуск видел бы код,
 * возвращался сразу и оставлял тип без группы НАВСЕГДА, отчитываясь при этом «посев готов». Тем же
 * способом протухала бы любая правка схемы в этом файле: посев зелёный, прогоны красные на старых
 * данных, а искать причину в посеве догадаешься последним. Ровно на этом шаблон обманул меня в
 * первой редакции скрипта.
 */
async function ensureType({ code, name, kind, schema, group, parentId = null }) {
  const found = await findType(code);
  if (found) {
    // Родителя доводим отдельно и по той же причине: он появился у типа не сразу (наследование
    // включили ради «Основы»), и база, посеянная прежней редакцией скрипта, осталась бы без него.
    if ((found.parentId ?? null) !== parentId)
      await api('PUT', `/document-types/${found.id}`, { name, code, parentId });
    await api('PUT', `/document-types/${found.id}/schema`, { schema: JSON.stringify(schema) });
    if (group) await api('PUT', `/document-types/${found.id}/group`, { group });
    return found.id;
  }
  const created = await api('POST', '/document-types', {
    name, code, kind, parentId, schema: JSON.stringify(schema), isAbstract: false,
  });
  if (group) await api('PUT', `/document-types/${created.id}/group`, { group });
  console.log(`  + тип «${name}»`);
  return created.id;
}

// ── Файлы-фикстуры ────────────────────────────────────────────────────────────
//
// Оба файла СОБИРАЮТСЯ ЗДЕСЬ, а не лежат бинарями рядом. Причин две. Первая: бинарь в репозитории
// нечем прочитать глазами — что в нём, приходится верить имени файла, а посев должен быть виден
// целиком. Вторая: он мгновенно перестаёт быть очевидно синтетическим, а репозиторий публичный, и
// «маленький скан для проверки» однажды окажется настоящим.

/** PNG 64×64: светлый квадрат в синей рамке. Сгенерирован раз и вставлен как есть — 146 байт. */
const LOGO_PNG_BASE64 =
  'iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAWUlEQVR42u3aMREAMAgEQZREGOow'
  + 'mT4ioGGyP2dg+4+TtboAABgE3CUDAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
  + 'AAAAAOgBPHcBPgQ8h5hICnAHHpwAAAAASUVORK5CYII=';

/**
 * Одностраничный PDF. Смещения объектов в таблице xref считаются при сборке: вписанные числом,
 * они разъезжаются от любой правки текста, и на выходе получается файл, который читалка молча
 * покажет пустым.
 */
function makePdf(title) {
  const stream = `BT /F1 18 Tf 60 760 Td (${title}) Tj ET\n`;
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] '
      + '/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>',
    `<< /Length ${stream.length} >>\nstream\n${stream}endstream`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];
  let pdf = '%PDF-1.4\n';
  const offsets = [];
  objects.forEach((body, i) => {
    offsets.push(pdf.length);
    pdf += `${i + 1} 0 obj\n${body}\nendobj\n`;
  });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`
    + offsets.map(o => `${String(o).padStart(10, '0')} 00000 n \n`).join('')
    + `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf, 'latin1');
}

/** Многочастная отправка файла. FormData/Blob есть в Node 18+ — отдельная зависимость не нужна. */
async function upload(path, bytes, fileName, mimeType) {
  const form = new FormData();
  form.append('file', new Blob([bytes], { type: mimeType }), fileName);
  const res = await fetch(`${API}/api${path}`, {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: form,
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`POST ${path} (файл) → ${res.status} ${text.slice(0, 300)}`);
  return JSON.parse(text);
}

/**
 * Записи каталога уровня «Система». Они здесь не «для полноты картины»: провайдер системных данных
 * предлагает кандидата на КАЖДЫЙ составной тип, у которого есть свои записи, и без записей
 * кандидатов нет вовсе. Тогда на странице наборов не появляется даже кнопка «Данные системы», а
 * диалогу источника нечего подставлять — обе проверки `pages-smoke` сообщили бы «проверять нечего».
 */
async function findEntry(displayName) {
  const all = await api('GET', '/common-data/for-scope?scope=System');
  return all.find(e => e.displayName === displayName) ?? null;
}

/** Запись каталога: заводит или ДОВОДИТ данные — по той же причине, что типы и документы выше. */
async function ensureEntry(compositeTypeId, displayName, data) {
  const found = await findEntry(displayName);
  if (found) {
    await api('PUT', `/common-data/${found.id}`, {
      displayName, data: JSON.stringify(data), aliases: [],
    });
    return found.id;
  }
  const created = await api('POST', '/common-data', {
    displayName, compositeTypeId, data: JSON.stringify(data),
    scope: 'System', scopeId: null, aliases: [],
  });
  console.log(`  + запись каталога «${displayName}»`);
  return created.id;
}

async function ensureCatalogEntries(orgTypeId, personTypeId) {
  for (const [displayName, data] of [
    ['ООО «Монтажэлектро»', { Наименование: 'ООО «Монтажэлектро»', ИНН: '7701000001' }],
    ['АО «Демо-Заказчик»', { Наименование: 'АО «Демо-Заказчик»', ИНН: '7701000002' }],
    ['ООО «Стройнадзор-Демо»', { Наименование: 'ООО «Стройнадзор-Демо»', ИНН: '7701000003' }],
  ]) await ensureEntry(orgTypeId, displayName, data);

  // Члены комиссии — кандидаты пикера ссылки. Их непустота проверяется отдельным утверждением:
  // на пустом списке «поиск сузил список» истинно при любом коде.
  for (const [displayName, data] of [
    ['Иванов И. И.', { ФИО: 'Иванов Иван Иванович', Должность: 'Производитель работ' }],
    ['Петров П. П.', { ФИО: 'Петров Пётр Петрович', Должность: 'Представитель заказчика' }],
    ['Сидоров С. С.', { ФИО: 'Сидоров Семён Семёнович', Должность: 'Технадзор' }],
  ]) await ensureEntry(personTypeId, displayName, data);
}

/**
 * Системный набор данных — тот, что открывает `pages-smoke` на странице наборов. Источников ему
 * НЕ добавляем нарочно: диалог «Добавить источник» подставляет ПЕРВОГО СВОБОДНОГО кандидата, и
 * набор с разобранными кандидатами оставил бы проверку без предмета.
 */
async function ensureSystemDataSet() {
  const files = await api('GET', '/datasets/files?scope=System');
  const found = files.find(f => f.name === 'Данные системы');
  if (found) return found.id;
  const created = await api('POST', '/datasets/files/system', {
    scope: 'System', scopeId: null, name: 'Данные системы',
  });
  console.log('  + системный набор «Данные системы»');
  return created.id;
}

/** Раздел и комплект под стройкой: комплект живёт в разделе, а не прямо в стройке. */
async function ensureSet(constructionId) {
  const withSections = async () => (await api('GET', `/constructions/${constructionId}`)).sections ?? [];
  let sections = await withSections();
  if (!sections.some(s => s.name === 'ЭОМ-1')) {
    await api('POST', `/constructions/${constructionId}/sections`, { name: 'ЭОМ-1' });
    console.log('  + раздел «ЭОМ-1»');
    sections = await withSections();
  }
  const section = sections.find(s => s.name === 'ЭОМ-1');
  const found = (section.documentSets ?? []).find(s => s.name === 'Демо-комплект');
  if (found) return found.id;
  const created = await api('POST', `/sections/${section.id}/sets`, { name: 'Демо-комплект' });
  console.log('  + комплект «Демо-комплект»');
  return created.id;
}

/**
 * Документ комплекта: заводит или ДОВОДИТ — имя и реквизиты пишем всегда, по той же причине, что
 * у типов выше (создание, переименование и заполнение — три отдельных запроса).
 *
 * Имя ищем среди уже существующих, а не заводим документ каждый раз: иначе повтор посева набивал
 * бы комплект копиями. Осколок оборванного запуска (документ создан, имя проставить не успели)
 * переиспользуем — без этого каждый обрыв оставлял бы в комплекте лишний безымянный документ.
 */
async function ensureDocument(setId, documentTypeId, name, requisites) {
  const set = await api('GET', `/document-sets/${setId}`);
  const inst = set.instances.find(i => i.name === name)
    ?? set.instances.find(i => !i.name && i.documentTypeId === documentTypeId)
    ?? await api('POST', `/document-sets/${setId}/documents`, { documentTypeId });
  await api('PUT', `/document-sets/${setId}/documents/${inst.id}/name`, { name });
  if (requisites) await api('PUT', `/document-sets/${setId}/documents/${inst.id}/requisites`, requisites);
  if (!set.instances.some(i => i.id === inst.id)) console.log(`  + документ «${name}»`);
  return inst.id;
}

const field = (key, title, type, extra = {}) => ({ key, title, type, required: false, ...extra });

async function main() {
  const version = await waitForApi();
  console.log(`Посев в ${API} (версия приложения ${version})`);

  await ensureAdmin();
  await ensureUser();
  const constructionId = await ensureConstruction();
  const primitiveTypeId = await ensurePrimitiveType();
  await ensureEnumType();

  // Составной тип — третья ветвь сводки поля («Организация»). Заодно даёт пикеру раздел
  // «Составные типы»: без хотя бы одного составного типа раздела в списке не будет.
  const orgTypeId = await ensureType({
    code: 'ORG_SEED', name: 'Организация', kind: 'Composite',
    schema: {
      fields: [
        field('Наименование', 'Наименование', 'string'),
        field('ИНН', 'ИНН', 'string'),
      ],
    },
  });

  // Люди в составе комиссии — тип строк массива-ссылок. Записи этого типа и есть кандидаты
  // пикера «Из каталога».
  const personTypeId = await ensureType({
    code: 'PERSON_SEED', name: 'Член комиссии', kind: 'Composite',
    schema: {
      fields: [
        field('ФИО', 'ФИО', 'string'),
        field('Должность', 'Должность', 'string'),
      ],
    },
  });

  // Цель ссылки в union-варианте «Документ». Вариант сделан ССЫЛКОЙ, а не строкой, нарочно:
  // проверка ищет содержимое активного варианта в ТЕКСТЕ диалога, а значение строкового поля
  // живёт в `input.value` — в тексте его нет, и проверка краснела бы на исправном компоненте.
  const normTypeId = await ensureType({
    code: 'NORM_SEED', name: 'Нормативный документ', kind: 'Composite',
    schema: {
      fields: [
        field('Обозначение', 'Обозначение', 'string'),
        field('Наименование', 'Наименование', 'string'),
      ],
    },
  });

  // Цель ссылки в union-варианте «Проект».
  const projectTypeId = await ensureType({
    code: 'PROJECT_SEED', name: 'Проект', kind: 'Composite',
    schema: {
      fields: [
        field('Шифр', 'Шифр', 'string'),
        field('Наименование', 'Наименование', 'string'),
      ],
    },
  });

  /**
   * Union-тип: обычный составной тип с тэгом `type.union` — «заполнено ровно одно из полей»
   * (issue #320). Порядок полей ЗНАЧИМ: «Проект» стоит вторым нарочно. Подмена активного варианта
   * подставляет ПЕРВЫЙ, и на строке, заполненной первым вариантом, дефект был бы неотличим от
   * исправности — проверка так и написана.
   */
  const unionTypeId = await ensureType({
    code: 'UNIONDOC_SEED', name: 'Документ произвольный', kind: 'Composite',
    schema: {
      tags: ['type.union'],
      fields: [
        field('Документ', 'Документ', 'complex', { typeId: normTypeId }),
        field('Проект', 'Проект', 'complex', { typeId: projectTypeId }),
      ],
    },
  });

  // Строка реестра работ. Колонок нужно не меньше трёх: проверка ширин двигает разделитель
  // ТРЕТЬЕЙ колонки, и на узкой таблице ей не за что взяться.
  const workRowTypeId = await ensureType({
    code: 'WORKROW_SEED', name: 'Строка работ', kind: 'Composite',
    schema: {
      fields: [
        field('Наименование', 'Наименование', 'string'),
        field('Единица', 'Единица', 'string'),
        field('Количество', 'Количество', 'number'),
        field('Примечание', 'Примечание', 'string'),
      ],
    },
  });

  // Тип-предок АОСР. Существует ради одного: без предков у типа нет и «Основы» — чип «Выбрать
  // основу» не рисуется вовсе, и проверка пикера основы осталась бы без предмета.
  const baseTypeId = await ensureType({
    code: 'ACT_BASE_SEED', name: 'Акт (основа)', kind: 'Document', group: 'Демо',
    schema: { fields: [field('ОбщееОснование', 'Общее основание', 'string')] },
  });

  // Главный тип прогона. Поля подобраны так, чтобы сводка показала ВСЕ ТРИ ветви разбора:
  // составной тип, тип из реестра и базовый скаляр. Ключ `ДатаНачалаРабот` проверка ищет
  // дословно — по нему она находит карточку поля после смены типа.
  const aosrId = await ensureType({
    code: 'AOSR_SEED', name: 'АОСР', kind: 'Document', group: 'Демо', parentId: baseTypeId,
    schema: {
      fields: [
        field('ДатаНачалаРабот', 'Дата начала работ', 'date'),
        field('Подрядчик', 'Подрядчик', 'complex', { typeId: orgTypeId }),
        field('КоличествоЭкземпляров', 'Количество экземпляров', 'primitive', { typeId: primitiveTypeId }),
        field('Примечание', 'Примечание', 'string'),
        field('ЧленыКомиссии', 'Члены комиссии', 'array', { typeId: personTypeId }),
        field('ДокументыСоответствия', 'Документы соответствия', 'array', { typeId: unionTypeId }),
      ],
      // Группы полей — это адреса, по которым проверки находят поля: дату ищут внутри «Дат работ»,
      // целочисленное поле — внутри «Прочего». Без групп обе искали бы по всей форме и могли бы
      // уехать на соседнее поле подходящего вида.
      groups: [
        { key: 'dates', title: 'Даты работ', fieldKeys: ['ДатаНачалаРабот'] },
        { key: 'other', title: 'Прочее', fieldKeys: ['КоличествоЭкземпляров', 'Примечание'] },
        // Разделу дано ИМЯ ПОЛЯ нарочно: проверка union'а сначала открывает раздел, потом
        // разворачивает в нём массив — то есть жмёт кнопку с этим именем ДВАЖДЫ. Лежи поле среди
        // безымянных «Основных реквизитов», второй кнопки не нашлось бы вовсе.
        { key: 'conformity', title: 'Документы соответствия', fieldKeys: ['ДокументыСоответствия'] },
      ],
    },
  });

  // Второй тип нужен ровно затем, чтобы БЫЛО КУДА уйти с несохранённой правкой: проверка
  // гарда переключается на него и ждёт вопроса «Несохранённые изменения».
  await ensureType({
    code: 'AOSR_APP_SEED', name: 'Приложение АОСР', kind: 'Document', group: 'Демо',
    schema: { fields: [field('Номер', 'Номер', 'string')] },
  });

  // Тип БЕЗ ШАБЛОНОВ. На нём стоит проверка «выбор не протёк с прежнего типа»: у типа без шаблонов
  // правая панель обязана предлагать выбрать или создать. Шаблонов ему не заводим — в этом вся
  // его роль, и первый же шаблон здесь молча обессмыслил бы проверку.
  await ensureType({
    code: 'ORDER_SEED', name: 'Приказ', kind: 'Document', group: 'Демо',
    schema: { fields: [field('Номер', 'Номер', 'string')] },
  });

  // Документ со ВСТРОЕННЫМИ строками массива: у АОСР члены комиссии хранятся ссылками, и там
  // пустая таблица — законный ответ, а здесь строки обязаны показаться все и сразу.
  const worksTypeId = await ensureType({
    code: 'WORKS_SEED', name: 'Реестр работ', kind: 'Document', group: 'Демо',
    schema: { fields: [field('Работы', 'Работы', 'array', { typeId: workRowTypeId })] },
  });

  // Тип с полем-файлом: предпросмотр вложения тянет байты из хранилища и показывает их
  // объект-URL'ом — проверить это можно только на настоящем файле в MinIO.
  const cableTypeId = await ensureType({
    code: 'CABLE_SEED', name: 'Кабельный журнал', kind: 'Document', group: 'Демо',
    schema: {
      fields: [
        field('Скан', 'Скан журнала', 'file'),
        field('Номер', 'Номер', 'string'),
      ],
    },
  });

  // Тип записи каталога с картинками. Группа названа ДОСЛОВНО так, как её ищет прогон: заголовок
  // раздела — это его адрес, а CSS-регистр к тексту в DOM отношения не имеет.
  const sroTypeId = await ensureType({
    code: 'SRO_SEED', name: 'Организация в СРО', kind: 'Composite',
    schema: {
      fields: [
        field('Наименование', 'Наименование', 'string'),
        field('Логотип', 'Логотип', 'image'),
        field('Печать', 'Печать', 'image'),
      ],
      groups: [{ key: 'stamps', title: 'ЛОГОТИП, ПЕЧАТЬ', fieldKeys: ['Логотип', 'Печать'] }],
    },
  });

  // Шаблон с ДВУМЯ версиями: проверка группировки требует, чтобы версии сложились под одно имя,
  // а на строке стояла самая свежая. С одной версией она сама сообщает «проверять нечего».
  // Каждый шаг доводит шаблон до нужного состояния ОТДЕЛЬНО, а не «имя занято — значит всё на
  // месте». Ровно на этом посев обманул меня первый раз: упав после создания первой версии, при
  // повторе он увидел имя и пропустил ВЕСЬ блок — шаблон остался без второй версии и без
  // параметров, а прогон сказал «проверять нечего», что легко принять за исправность.
  const templatesOf = async () => (await api('GET', `/templates?documentTypeId=${aosrId}`))
    .filter(t => t.name === 'Основной');
  let seedTemplate = (await templatesOf())[0];
  if (!seedTemplate) {
    seedTemplate = await api('POST', '/templates', {
      documentTypeId: aosrId, name: 'Основной',
      content: '#set page(paper: "a4")\n= АОСР\n\nДемонстрационный шаблон посева.\n',
    });
    console.log('  + шаблон «Основной»');
  }
  if ((await templatesOf()).length < 2) {
    await api('POST', `/templates/${seedTemplate.id}/versions`, {
      content: '#set page(paper: "a4")\n= АОСР\n\nВторая версия шаблона посева.\n',
      comment: 'Вторая версия для проверки группировки',
    });
    console.log('  + вторая версия шаблона');
  }
  // Объявление параметров — отдельным полем; проверка ждёт у панели число в скобках.
  if (!(await templatesOf()).some(t => t.parameters)) {
    for (const v of await templatesOf()) {
      await api('PUT', `/templates/${v.id}/parameters`, {
        parameters: JSON.stringify([
          { name: 'showLogo', label: 'Печатать логотип', type: 'boolean', default: true },
          { name: 'copies', label: 'Экземпляров', type: 'number', default: 2 },
        ]),
      });
    }
    console.log('  + параметры шаблона');
  }
  // Почта: проверка смотрит именно адрес отправителя. Порт для неё не годится — у него есть
  // разумное умолчание, и пустая форма выглядела бы заполненной.
  // Читаем настройки целиком: отдельного GET у почты нет, а `.catch(() => null)` поверх 404
  // делал бы «уже настроено» неотличимым от «спросили не туда» — посев переписывал бы почту
  // каждый запуск и молчал бы о том, что проверяет несуществующий адрес.
  const settings = await api('GET', '/settings/integrations');
  if (!settings?.smtp?.from) {
    await api('PUT', '/settings/integrations/email', {
      enabled: false, host: 'smtp.example.test', port: 587,
      user: 'seed@example.test', password: '', from: 'seed@example.test',
      fromName: 'Посев прогонов', useSsl: true,
    });
    console.log('  + настройки почты');
  }

  await ensureCatalogEntries(orgTypeId, personTypeId);
  await ensureSystemDataSet();

  // Цель ссылки union-варианта «Проект»: имя проверка ищет в открытом варианте дословно.
  const projectEntryId = await ensureEntry(projectTypeId, 'Проект ЭОМ-1', {
    Шифр: '250701-ЭОМ', Наименование: 'Электроосвещение и силовое оборудование',
  });

  const normEntryId = await ensureEntry(normTypeId, 'ПУЭ 7 (издание седьмое)', {
    Обозначение: 'ПУЭ 7', Наименование: 'Правила устройства электроустановок',
  });

  // Картинку загружаем ТОЛЬКО если её ещё нет: повторный посев иначе плодил бы в хранилище копии
  // одного и того же логотипа, а идемпотентность здесь — не украшение: посев гоняется и на живой базе.
  const sroEntry = await findEntry('Техногид');
  const storedLogo = sroEntry?.data?.Логотип;
  const logo = storedLogo?.$type === 'image' ? storedLogo : await (async () => {
    const up = await upload('/attachments/image', Buffer.from(LOGO_PNG_BASE64, 'base64'),
      'logo-seed.png', 'image/png');
    console.log('  + картинка логотипа в хранилище');
    return { $type: 'image', blobPath: up.blobPath, fileName: up.fileName, mimeType: up.mimeType, width: '3cm' };
  })();
  await ensureEntry(sroTypeId, 'Техногид', { Наименование: 'ООО «Техногид»', Логотип: logo });

  // Комплект с документами. Имена документов повторяют те, что прогоны ищут ДОСЛОВНО: их шифр
  // («250701.ЭОМ-1.АОСР») — единственная примета, по которой проверка находит нужную строку.
  const setId = await ensureSet(constructionId);
  const aosrInstanceId = await ensureDocument(setId, aosrId, '250701.ЭОМ-1.АОСР', {
    // Дата обязана быть ЗАПОЛНЕНА: проверка поля даты ищет сохранённый год и на пустом значении
    // сообщила бы «нечего откатывать», оставшись зелёной при сломанном компоненте.
    ДатаНачалаРабот: '2026-07-01',
    Примечание: 'Документ посева живых прогонов',
    КоличествоЭкземпляров: 2,
    ЧленыКомиссии: [],
    // Две строки union'а, заполненные РАЗНЫМИ вариантами: по строке видно, какой вариант обязан
    // открыться активным. Одинаковые варианты в обеих строках оставили бы проверку слепой к тому,
    // что активный вариант берётся не из строки.
    ДокументыСоответствия: [
      { Проект: { $ref: 'catalog', entryId: projectEntryId, displayName: 'Проект ЭОМ-1', scope: 'System' } },
      { Документ: { $ref: 'catalog', entryId: normEntryId, displayName: 'ПУЭ 7 (издание седьмое)', scope: 'System' } },
    ],
  });

  // Основа для АОСР: кандидатом её делает ТИП-ПРЕДОК, а не имя, — документ типа «Акт (основа)»
  // в том же комплекте.
  await ensureDocument(setId, baseTypeId, '250701.ЭОМ-1.Акт (основа)', {
    ОбщееОснование: 'Рабочая документация 250701-ЭОМ',
  });

  // Девятнадцать строк — число из проверки: она сверяет ровно его, потому что «строк больше нуля»
  // проходило и на таблице, которая показывала первую страницу вместо всех данных.
  await ensureDocument(setId, worksTypeId, '250701.ЭОМ-1.1.Реестр работ', {
    Работы: Array.from({ length: 19 }, (_, i) => ({
      Наименование: `Прокладка кабеля, участок ${i + 1}`,
      Единица: 'м',
      Количество: 10 * (i + 1),
      Примечание: i % 3 === 0 ? 'по проекту' : '',
    })),
  });

  // Вложение: тот же принцип, что у картинки — грузим, только если его ещё нет.
  const cableDocName = 'Кабельный журнал (из PDF)';
  const cableExisting = (await api('GET', `/document-sets/${setId}`))
    .instances.find(i => i.name === cableDocName);
  const scan = cableExisting?.requisites?.Скан?.$type === 'file'
    ? cableExisting.requisites.Скан
    : await (async () => {
      const up = await upload('/attachments', makePdf('BHS.CRG seed attachment'),
        'cable-journal-seed.pdf', 'application/pdf');
      console.log('  + вложение-PDF в хранилище');
      return { $type: 'file', blobPath: up.blobPath, fileName: up.fileName, mimeType: up.mimeType, size: up.size };
    })();
  await ensureDocument(setId, cableTypeId, cableDocName, { Скан: scan, Номер: 'КЖ-1' });

  console.log('\nПосев готов. Переменные прогонов:');
  console.log(`SMOKE_CONSTRUCTION_ID=${constructionId}`);
  console.log(`SMOKE_SET_ID=${setId}`);
  console.log(`SMOKE_INSTANCE_ID=${aosrInstanceId}`);
}

main().catch(e => { console.error('ПОСЕВ УПАЛ:', e.message); process.exit(1); });
