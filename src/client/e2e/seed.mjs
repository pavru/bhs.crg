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
async function ensureType({ code, name, kind, schema, group }) {
  const found = await findType(code);
  if (found) {
    await api('PUT', `/document-types/${found.id}/schema`, { schema: JSON.stringify(schema) });
    if (group) await api('PUT', `/document-types/${found.id}/group`, { group });
    return found.id;
  }
  const created = await api('POST', '/document-types', {
    name, code, kind, parentId: null, schema: JSON.stringify(schema), isAbstract: false,
  });
  if (group) await api('PUT', `/document-types/${created.id}/group`, { group });
  console.log(`  + тип «${name}»`);
  return created.id;
}

/**
 * Записи каталога уровня «Система». Они здесь не «для полноты картины»: провайдер системных данных
 * предлагает кандидата на КАЖДЫЙ составной тип, у которого есть свои записи, и без записей
 * кандидатов нет вовсе. Тогда на странице наборов не появляется даже кнопка «Данные системы», а
 * диалогу источника нечего подставлять — обе проверки `pages-smoke` сообщили бы «проверять нечего».
 */
async function ensureCatalogEntries(orgTypeId) {
  const existing = await api('GET', '/common-data/for-scope?scope=System');
  const orgs = [
    ['ООО «Монтажэлектро»', { Наименование: 'ООО «Монтажэлектро»', ИНН: '7701000001' }],
    ['АО «Демо-Заказчик»', { Наименование: 'АО «Демо-Заказчик»', ИНН: '7701000002' }],
    ['ООО «Стройнадзор-Демо»', { Наименование: 'ООО «Стройнадзор-Демо»', ИНН: '7701000003' }],
  ];
  let added = 0;
  for (const [displayName, data] of orgs) {
    if (existing.some(e => e.displayName === displayName)) continue;
    await api('POST', '/common-data', {
      displayName, compositeTypeId: orgTypeId, data: JSON.stringify(data),
      scope: 'System', scopeId: null, aliases: [],
    });
    added++;
  }
  if (added) console.log(`  + записей каталога: ${added}`);
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

  // Главный тип прогона. Поля подобраны так, чтобы сводка показала ВСЕ ТРИ ветви разбора:
  // составной тип, тип из реестра и базовый скаляр. Ключ `ДатаНачалаРабот` проверка ищет
  // дословно — по нему она находит карточку поля после смены типа.
  const aosrId = await ensureType({
    code: 'AOSR_SEED', name: 'АОСР', kind: 'Document', group: 'Демо',
    schema: {
      fields: [
        field('ДатаНачалаРабот', 'Дата начала работ', 'date'),
        field('Подрядчик', 'Подрядчик', 'complex', { typeId: orgTypeId }),
        field('КоличествоЭкземпляров', 'Количество экземпляров', 'primitive', { typeId: primitiveTypeId }),
        field('Примечание', 'Примечание', 'string'),
      ],
      // Группа полей нужна `shared-ui`: поле даты он ищет НЕ по всей форме, а внутри раздела
      // «Даты работ» — раздел и есть адрес, по которому проверка находит именно дату, а не первый
      // попавшийся input с четырьмя цифрами.
      groups: [{ key: 'dates', title: 'Даты работ', fieldKeys: ['ДатаНачалаРабот'] }],
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

  await ensureCatalogEntries(orgTypeId);
  await ensureSystemDataSet();

  // Комплект с документами. Имена документов повторяют те, что прогоны ищут ДОСЛОВНО: их шифр
  // («250701.ЭОМ-1.АОСР») — единственная примета, по которой проверка находит нужную строку.
  const setId = await ensureSet(constructionId);
  const aosrInstanceId = await ensureDocument(setId, aosrId, '250701.ЭОМ-1.АОСР', {
    // Дата обязана быть ЗАПОЛНЕНА: проверка поля даты ищет сохранённый год и на пустом значении
    // сообщила бы «нечего откатывать», оставшись зелёной при сломанном компоненте.
    ДатаНачалаРабот: '2026-07-01',
    Примечание: 'Документ посева живых прогонов',
  });

  console.log('\nПосев готов. Переменные прогонов:');
  console.log(`SMOKE_CONSTRUCTION_ID=${constructionId}`);
  console.log(`SMOKE_SET_ID=${setId}`);
  console.log(`SMOKE_INSTANCE_ID=${aosrInstanceId}`);
}

main().catch(e => { console.error('ПОСЕВ УПАЛ:', e.message); process.exit(1); });
