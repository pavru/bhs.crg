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
 *
 * ⚠️ ИМЕНА ТИПОВ несут суффикс «(посев)» и обязаны сортироваться ПОСЛЕ «АОСР (посев)». Обе части
 * не украшение:
 *
 * 1. Имя типа документа уникально НАРАВНЕ с кодом (`EnsureUnique`). Посев ищет свой тип по КОДУ,
 *    не находит и заводит новый — а тот падает с 400 «тип с именем уже существует». В рабочей базе
 *    восемь имён из этого файла заняты (проверено запросом): «АОСР», «Организация», «Приказ»,
 *    «Кабельный журнал», «Проект»… Без суффикса посев на живой базе умирал бы на первом же из них,
 *    успев переписать схемы предыдущих, — а README зовёт натравливать его на свою базу.
 * 2. Страница типов выбирает ПЕРВЫЙ по имени (`localeCompare('ru')`) сама, и `types-smoke` смотрит
 *    сводку полей именно у него, ожидая АОСР. «Акт (основа)» сортировался перед ним (к < о), и
 *    прогон в CI упал 4/9: открывался тип, где нет ни составного поля, ни поля из реестра.
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

/**
 * CSV счёта — сырьё набора данных, на котором стоят проверки материализации. Колонок нужно не
 * меньше одной непустой: диалог маппит колонку источника в поле варианта union'а, и на источнике
 * без колонок проверка сообщила бы «нечего маппить», оставшись зелёной при сломанном диалоге.
 *
 * Разделитель — запятая: парсер выбирает его по первой строке (табуляция, иначе запятая).
 */
const INVOICE_CSV = [
  'Артикул,Наименование,Количество,Цена',
  'КГ-3х2.5,"Кабель ВВГнг(А)-LS 3х2,5",250,89.40',
  'ЛТ-40,Лоток лестничный 400х80,36,1240.00',
  'АВ-16,Автоматический выключатель C16,12,410.50',
].join('\n');

/**
 * Лежит ли файл в хранилище НА САМОМ ДЕЛЕ. Значение в базе этого не доказывает: хранилище живёт в
 * контейнере и пересоздаётся отдельно от Postgres (`docker compose down -v`), а запись с путём
 * при этом остаётся. Посев тогда отчитался бы «готов», ничего не загрузив, а прогон упал бы с
 * «картинка не разобралась» — сообщением, которое показывает на компонент, а не на пустое хранилище.
 */
async function blobExists(blobPath) {
  const res = await fetch(`${API}/api/attachments?path=${encodeURIComponent(blobPath)}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  await res.arrayBuffer().catch(() => {});   // тело дочитываем, иначе соединение висит
  return res.ok;
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
async function findEntry(displayName, compositeTypeId) {
  const all = await api('GET', '/common-data/for-scope?scope=System');
  return all.find(e => e.displayName === displayName
    && (!compositeTypeId || e.compositeTypeId === compositeTypeId)) ?? null;
}

/**
 * Запись каталога: заводит или ДОВОДИТ данные — по той же причине, что типы и документы выше.
 *
 * Совпадение ищется по имени И ТИПУ. Одного имени мало, и это не теория: доводка — это PUT,
 * который заменяет данные и алиасы ЦЕЛИКОМ, а посев разрешено натравливать на свою базу. «Иванов
 * И. И.» или «ПУЭ 7» там встречаются сами собой; совпади имя — живую запись молча заменило бы
 * двухполевой синтетикой, и узнать об этом было бы неоткуда. Тип посева свой, поэтому чужая
 * запись под тем же именем просто не считается найденной.
 */
async function ensureEntry(compositeTypeId, displayName, data) {
  const found = await findEntry(displayName, compositeTypeId);
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
 * Набор данных из ФАЙЛА (в отличие от системного): загрузка многочастная, и полей у неё больше
 * одного — общий `upload` сюда не годится, он шлёт только сам файл.
 *
 * Идемпотентность здесь дороже, чем у остальных: повторная загрузка не «перезапишет запись», а
 * положит в хранилище второй файл и заведёт второй набор с тем же именем — и `dialogs-smoke`,
 * который ищет набор ПО ИМЕНИ, открывал бы первый попавшийся из двух.
 */
async function ensureDataSetFile(name, bytes, fileName, mimeType) {
  const files = await api('GET', '/datasets/files?scope=System');
  const found = files.find(f => f.name === name);
  if (found) return found.id;

  const form = new FormData();
  form.append('file', new Blob([bytes], { type: mimeType }), fileName);
  form.append('name', name);
  form.append('scope', 'System');
  const res = await fetch(`${API}/api/datasets/files`, {
    method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: form,
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`POST /datasets/files → ${res.status} ${text.slice(0, 300)}`);
  console.log(`  + набор данных «${name}»`);
  return JSON.parse(text).id;
}

/**
 * Источник набора. Для CSV `sheetOrPath` разбором не используется вовсе (парсер читает файл
 * целиком), но поле обязательное — шлём канонический маркер `default`, тот же, что подставляет
 * определение источников.
 *
 * Колонки НЕ передаются: сервер разбирает файл при создании источника и записывает схему сам.
 * Именно она и нужна проверке маппинга — без колонок селектор в диалоге материализации пуст, и
 * проверка сообщила бы «нечего маппить».
 */
async function ensureDataSetSource(fileId, name) {
  const sources = await api('GET', `/datasets/files/${fileId}/sources`);
  const found = sources.find(s => s.name === name);
  if (found) return found.id;
  const created = await api('POST', `/datasets/files/${fileId}/sources`, {
    name, sheetOrPath: 'default', columnExpressions: null,
  });
  console.log(`  + источник «${name}»`);
  return created.id;
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
async function ensureSet(constructionId, sectionName, setName) {
  const withSections = async () => (await api('GET', `/constructions/${constructionId}`)).sections ?? [];
  let sections = await withSections();
  if (!sections.some(s => s.name === sectionName)) {
    await api('POST', `/constructions/${constructionId}/sections`, { name: sectionName });
    console.log(`  + раздел «${sectionName}»`);
    sections = await withSections();
  }
  const section = sections.find(s => s.name === sectionName);
  // Раздел только что создан или уже был — не найтись он может лишь при сбое на предыдущем шаге.
  // Молча свалиться на `section.documentSets` значит отчитаться «Cannot read properties of
  // undefined», то есть не назвать ничего.
  if (!section) throw new Error(`Раздел «${sectionName}» не найден в стройке после создания`);
  const found = (section.documentSets ?? []).find(s => s.name === setName);
  if (found) return found.id;
  const created = await api('POST', `/sections/${section.id}/sets`, { name: setName });
  console.log(`  + комплект «${setName}»`);
  return created.id;
}

/**
 * Документ комплекта: заводит или ДОВОДИТ — имя и реквизиты пишем всегда, по той же причине, что
 * у типов выше (создание, переименование и заполнение — три отдельных запроса).
 *
 * Имя ищем среди уже существующих, а не заводим документ каждый раз: иначе повтор посева набивал
 * бы комплект копиями. Ищем имя ВМЕСТЕ С ТИПОМ: переедь документ посева на другой тип, найденный
 * по одному имени получил бы реквизиты чужой формы, а его адрес уехал бы в `SMOKE_INSTANCE_ID` —
 * и проверка ссылки открывала бы не тот документ. Осколок оборванного запуска (документ создан,
 * имя проставить не успели) переиспользуем — без этого каждый обрыв оставлял бы в комплекте
 * лишний безымянный документ.
 */
async function ensureDocument(setId, documentTypeId, name, requisites) {
  const set = await api('GET', `/document-sets/${setId}`);
  const inst = set.instances.find(i => i.name === name && i.documentTypeId === documentTypeId)
    ?? set.instances.find(i => !i.name && i.documentTypeId === documentTypeId)
    ?? await api('POST', `/document-sets/${setId}/documents`, { documentTypeId });
  await api('PUT', `/document-sets/${setId}/documents/${inst.id}/name`, { name });
  if (requisites) await api('PUT', `/document-sets/${setId}/documents/${inst.id}/requisites`, requisites);
  if (!set.instances.some(i => i.id === inst.id)) console.log(`  + документ «${name}»`);
  return inst.id;
}

const field = (key, title, type, extra = {}) => ({ key, title, type, required: false, ...extra });

/**
 * Имена, которые прогон ищет ТОЧНЫМ совпадением, — константами, потому что каждое из них
 * называется в двух местах: при заведении объекта и в строке `SMOKE_*` для прогона. Разъехавшись,
 * эти два места не поссорятся ни с типами, ни с сервером — посев отчитается «готов», а прогон
 * скажет «в пикере нет такого типа», и причину придётся искать в последнем месте, где её ждёшь.
 */
const NAME = {
  unionType: 'Документ произвольный (посев)',
  worksUnionType: 'Работы АОСР (посев)',
  materialsUnionType: 'Материалы АОСР (посев)',
  datasetFile: 'Счет на оплату (посев)',
  materialsDoc: '250701.ЭОМ-1.2.Реестр материалов',
};

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
    code: 'ORG_SEED', name: 'Организация (посев)', kind: 'Composite',
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
    code: 'PERSON_SEED', name: 'Член комиссии (посев)', kind: 'Composite',
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
    code: 'NORM_SEED', name: 'Нормативный документ (посев)', kind: 'Composite',
    schema: {
      fields: [
        field('Обозначение', 'Обозначение', 'string'),
        field('Наименование', 'Наименование', 'string'),
      ],
    },
  });

  // Цель ссылки в union-варианте «Проект».
  const projectTypeId = await ensureType({
    code: 'PROJECT_SEED', name: 'Проект (посев)', kind: 'Composite',
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
    code: 'UNIONDOC_SEED', name: NAME.unionType, kind: 'Composite',
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
    code: 'WORKROW_SEED', name: 'Строка работ (посев)', kind: 'Composite',
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
  // Имя не «Акт (основа)» именно поэтому: оно сортировалось перед «АОСР» и уводило страницу типов
  // на себя (правило и разбор — у `ensureType`).
  const baseTypeId = await ensureType({
    code: 'ACT_BASE_SEED', name: 'Основа акта (посев)', kind: 'Document', group: 'Демо',
    schema: { fields: [field('ОбщееОснование', 'Общее основание', 'string')] },
  });

  // Главный тип прогона. Поля подобраны так, чтобы сводка показала ВСЕ ТРИ ветви разбора:
  // составной тип, тип из реестра и базовый скаляр. Ключ `ДатаНачалаРабот` проверка ищет
  // дословно — по нему она находит карточку поля после смены типа.
  const aosrId = await ensureType({
    code: 'AOSR_SEED', name: 'АОСР (посев)', kind: 'Document', group: 'Демо', parentId: baseTypeId,
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
    code: 'AOSR_APP_SEED', name: 'Приложение АОСР (посев)', kind: 'Document', group: 'Демо',
    schema: { fields: [field('Номер', 'Номер', 'string')] },
  });

  // Тип БЕЗ ШАБЛОНОВ. На нём стоит проверка «выбор не протёк с прежнего типа»: у типа без шаблонов
  // правая панель обязана предлагать выбрать или создать. Шаблонов ему не заводим — в этом вся
  // его роль, и первый же шаблон здесь молча обессмыслил бы проверку.
  await ensureType({
    code: 'ORDER_SEED', name: 'Приказ (посев)', kind: 'Document', group: 'Демо',
    schema: { fields: [field('Номер', 'Номер', 'string')] },
  });

  // Документ со ВСТРОЕННЫМИ строками массива: у АОСР члены комиссии хранятся ссылками, и там
  // пустая таблица — законный ответ, а здесь строки обязаны показаться все и сразу.
  const worksTypeId = await ensureType({
    code: 'WORKS_SEED', name: 'Реестр работ (посев)', kind: 'Document', group: 'Демо',
    schema: { fields: [field('Работы', 'Работы', 'array', { typeId: workRowTypeId })] },
  });

  // Тип с полем-файлом: предпросмотр вложения тянет байты из хранилища и показывает их
  // объект-URL'ом — проверить это можно только на настоящем файле в MinIO.
  const cableTypeId = await ensureType({
    code: 'CABLE_SEED', name: 'Кабельный журнал (посев)', kind: 'Document', group: 'Демо',
    schema: {
      fields: [
        field('Скан', 'Скан журнала', 'file'),
        field('Номер', 'Номер', 'string'),
      ],
    },
  });

  // ── Документы качества и материалы (dialogs-smoke) ──────────────────────────
  //
  // Типов качества нужно ДВА, и оба — с говорящими именами. Пикер поиска подставляет умолчанием
  // тип, чьё имя похоже на «сертификат» (`/сертификат/i` в LinkPickerModal), а проверка «выбор
  // пережил закрытие» выбирает ДРУГОЙ и сверяет, что вернулся именно он. С одним типом выбирать
  // было бы не из чего, и проверка сообщила бы «второго типа в пикере нет».
  await ensureType({
    code: 'CERT_SEED', name: 'Сертификат соответствия (посев)', kind: 'Document', group: 'Демо',
    schema: {
      tags: ['type.qualityDocument'],
      fields: [
        field('Номер', 'Номер', 'string', { tags: ['doc.number'] }),
        field('ДействуетДо', 'Действует до', 'date', { tags: ['quality.validUntil'] }),
        field('Изготовитель', 'Изготовитель', 'string', { tags: ['quality.manufacturer'] }),
      ],
    },
  });
  await ensureType({
    code: 'DECL_SEED', name: 'Декларация о соответствии (посев)', kind: 'Document', group: 'Демо',
    schema: {
      tags: ['type.qualityDocument'],
      fields: [
        field('Номер', 'Номер', 'string', { tags: ['doc.number'] }),
        field('ДействуетДо', 'Действует до', 'date', { tags: ['quality.validUntil'] }),
      ],
    },
  });

  /**
   * Строка материала. «Материальным» тип делает НЕ имя, а поле с тэгом `material.qualityDocLink`
   * (`isMaterialType`): по нему редактор документа и решает, показывать ли вкладку «Документы
   * качества». Тэг применим только к полю типа `complex`, и `typeId` ему не нужен — подмешивается
   * туда документ качества любого типа.
   *
   * Поля идентичности пронумерованы: из них складывается ключ связки «материал → документ», и
   * порядок компонентов задаёт именно номер (issue #663).
   */
  const materialRowTypeId = await ensureType({
    code: 'MATROW_SEED', name: 'Строка материала (посев)', kind: 'Composite',
    schema: {
      fields: [
        field('Артикул', 'Артикул', 'string', { tags: ['identity:1'] }),
        field('Наименование', 'Наименование', 'string', { tags: ['identity:2'] }),
        field('Количество', 'Количество', 'number'),
        field('ДокументКачества', 'Документ качества', 'complex', { tags: ['material.qualityDocLink'] }),
      ],
    },
  });

  // Документ с материалами: без него вкладки «Документы качества» нет вовсе, а с ней нет и пикера,
  // на котором стоят обе проверки типа для веб-поиска.
  const materialsTypeId = await ensureType({
    code: 'MATREG_SEED', name: 'Реестр материалов (посев)', kind: 'Document', group: 'Демо',
    schema: { fields: [field('Материалы', 'Материалы', 'array', { typeId: materialRowTypeId })] },
  });

  /**
   * Два union-типа для проверки «смена типа снимает пометку варианта». Второй вариант у обоих
   * называется ОДИНАКОВО — «Реестр», и это главное в них.
   *
   * Совпадение ключей и делает проверку способной краснеть: пометка выбранного варианта хранится
   * ключом, и при разных ключах она отбросилась бы сама собой — дефект был бы неотличим от
   * исправности. Ровно это поймало ревью PR #862 на прежней паре типов.
   *
   * Порядок полей значим: первый вариант — тот, на котором тип обязан открыться после смены.
   */
  await ensureType({
    code: 'AOSRWORKS_SEED', name: NAME.worksUnionType, kind: 'Composite',
    schema: {
      tags: ['type.union'],
      fields: [
        field('Работы', 'Работы', 'complex', { typeId: workRowTypeId }),
        field('Реестр', 'Реестр', 'complex', { typeId: normTypeId }),
      ],
    },
  });
  await ensureType({
    code: 'AOSRMAT_SEED', name: NAME.materialsUnionType, kind: 'Composite',
    schema: {
      tags: ['type.union'],
      fields: [
        field('Материалы', 'Материалы', 'complex', { typeId: materialRowTypeId }),
        field('Реестр', 'Реестр', 'complex', { typeId: normTypeId }),
      ],
    },
  });

  // Тип записи каталога с картинками. Группа названа ДОСЛОВНО так, как её ищет прогон: заголовок
  // раздела — это его адрес, а CSS-регистр к тексту в DOM отношения не имеет.
  const sroTypeId = await ensureType({
    code: 'SRO_SEED', name: 'Организация в СРО (посев)', kind: 'Composite',
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
  const sroEntry = await findEntry('Техногид', sroTypeId);
  const storedLogo = sroEntry?.data?.Логотип;
  const logoOnDisk = storedLogo?.$type === 'image' && await blobExists(storedLogo.blobPath);
  const logo = logoOnDisk ? storedLogo : await (async () => {
    const up = await upload('/attachments/image', Buffer.from(LOGO_PNG_BASE64, 'base64'),
      'logo-seed.png', 'image/png');
    console.log('  + картинка логотипа в хранилище');
    return { $type: 'image', blobPath: up.blobPath, fileName: up.fileName, mimeType: up.mimeType, width: '3cm' };
  })();
  await ensureEntry(sroTypeId, 'Техногид', { Наименование: 'ООО «Техногид»', Логотип: logo });

  // Комплект с документами. Имена документов повторяют те, что прогоны ищут ДОСЛОВНО: их шифр
  // («250701.ЭОМ-1.АОСР») — единственная примета, по которой проверка находит нужную строку.
  const setId = await ensureSet(constructionId, 'ЭОМ-1', 'Демо-комплект');
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
  await ensureDocument(setId, baseTypeId, '250701.ЭОМ-1.Основа акта', {
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
  const storedScan = cableExisting?.requisites?.Скан;
  const scanOnDisk = storedScan?.$type === 'file' && await blobExists(storedScan.blobPath);
  const scan = scanOnDisk ? storedScan : await (async () => {
      const up = await upload('/attachments', makePdf('BHS.CRG seed attachment'),
        'cable-journal-seed.pdf', 'application/pdf');
      console.log('  + вложение-PDF в хранилище');
      return { $type: 'file', blobPath: up.blobPath, fileName: up.fileName, mimeType: up.mimeType, size: up.size };
    })();
  await ensureDocument(setId, cableTypeId, cableDocName, { Скан: scan, Номер: 'КЖ-1' });

  /**
   * Реестр материалов — предмет обеих проверок пикера документа качества. Строки заведены
   * ВСТРОЕННЫМИ и НЕПРИВЯЗАННЫМИ нарочно: кнопка «Связать» стоит в непривязанной строке, и на
   * реестре со связками её бы просто не было.
   *
   * Имя документа проверка ищет дословно — оно и есть её единственная примета в списке.
   */
  await ensureDocument(setId, materialsTypeId, NAME.materialsDoc, {
    Материалы: [
      { Артикул: 'КГ-3х2.5', Наименование: 'Кабель ВВГнг(А)-LS 3х2,5', Количество: 250 },
      { Артикул: 'ЛТ-40', Наименование: 'Лоток лестничный 400х80', Количество: 36 },
      { Артикул: 'АВ-16', Наименование: 'Автоматический выключатель C16', Количество: 12 },
    ],
  });

  /**
   * Второй комплект — цель копирования. Пикер «Скопировать в комплект» ИСКЛЮЧАЕТ текущий, поэтому
   * на одном комплекте в базе список пуст, и обе проверки копирования сообщили бы «в пикере нет ни
   * одного комплекта-цели». Раздел свой: комплект-близнец внутри «ЭОМ-1» отличался бы от исходного
   * только именем, а так в пикере видно и раздел.
   */
  const copyTargetSetId = await ensureSet(constructionId, 'СКС-1', 'Демо-комплект СКС');

  /**
   * Набор данных с источниками — предмет четырёх проверок материализации. Источника ДВА: диалог
   * открывается из кебаба ПЕРВОГО источника, а второй держит форму экрана той же, что на живой
   * базе, где у счёта разобраны и шапка, и товары.
   *
   * Файл — CSV, а не PDF: колонки нужны настоящие, а PDF-источник получает их распознаванием,
   * то есть ИИ-движком, которого в CI нет (см. `e2e/README.md`).
   */
  const invoiceFileId = await ensureDataSetFile(
    NAME.datasetFile, Buffer.from(INVOICE_CSV, 'utf8'), 'invoice-seed.csv', 'text/csv');
  await ensureDataSetSource(invoiceFileId, 'Товары счёта');
  await ensureDataSetSource(invoiceFileId, 'Шапка счёта');

  console.log('\nПосев готов. Переменные прогонов:');
  console.log(`SMOKE_CONSTRUCTION_ID=${constructionId}`);
  console.log(`SMOKE_SET_ID=${setId}`);
  console.log(`SMOKE_INSTANCE_ID=${aosrInstanceId}`);
  // Комплект-цель прогонам не адресуется (пикер ищет его по имени), но напечатан: строка в логе —
  // единственное место, где видно, что цель копирования вообще создалась.
  console.log(`SMOKE_COPY_TARGET_SET_ID=${copyTargetSetId}`);
  /**
   * ИМЕНА типов — тоже переменные прогона, наравне с адресами. Прогон кликает по имени типа
   * ТОЧНЫМ совпадением, а имена посева несут суффикс «(посев)»: без него посев умирал бы на
   * занятом имени в рабочей базе (см. `ensureType`). Умолчания в самом прогоне — имена ЖИВОЙ базы,
   * где он и запускается руками; здесь они замещаются посеянными.
   */
  console.log(`SMOKE_UNION_TYPE=${NAME.unionType}`);
  console.log(`SMOKE_WORKS_UNION_TYPE=${NAME.worksUnionType}`);
  console.log(`SMOKE_MATERIALS_UNION_TYPE=${NAME.materialsUnionType}`);
  console.log(`SMOKE_DATASET_FILE=${NAME.datasetFile}`);
  console.log(`SMOKE_MATERIALS_DOC=${NAME.materialsDoc}`);
  // ⚠️ `SMOKE_PDF_FILE_ID` здесь НЕ печатается, и это осознанно. Набора с распознанными страницами
  // посев не создаёт (распознаёт их ИИ-движок), но объявляет это не он, а сама работа CI —
  // переменной уровня работы. Причина в порядке сильнее-слабее: переменная работы перекрывает
  // дописанную в `$GITHUB_ENV`, поэтому напечатай посев своё значение, его приняли бы и МОЛЧА
  // проигнорировали. Один факт — одно место, и это место `ci.yml`.
}

main().catch(e => { console.error('ПОСЕВ УПАЛ:', e.message); process.exit(1); });
