// Посев шаблона для живого прогона предпросмотра документа (issue #1493).
//
// Отдельным файлом от `seed.mjs` по храповику размера: тот у своего уровня, и строка, добавленная
// туда, стоила бы строки, отнятой у чужой проверки.
//
// Шаблон — у посевного «Кабельного журнала» (тип CABLE_SEED, документ «Кабельный журнал (из PDF)»):
// его редактор `fields-smoke` уже открывает. Предпросмотр строится по шаблону по умолчанию, а без него
// — по первому активному; своего шаблона у типа не было, и панель говорила «Нет шаблона».

/** Имя шаблона; по нему посев узнаёт, что шаблон уже есть. */
export const PREVIEW_TEMPLATE = 'Предпросмотр (посев)';

/** Листов в документе: проверке нужен лист не первый, чтобы прыжок на первый был виден. */
export const PREVIEW_PAGES = 4;

/**
 * Номер журнала печатается на КАЖДОМ листе: по слою текста второго листа проверка узнаёт, что
 * на экране уже новый вариант, а не прежний.
 */
const CONTENT = `#set page(paper: "a4")
#let d = json("data.json")
#for i in range(1, ${PREVIEW_PAGES + 1}) [
  = Лист #i из ${PREVIEW_PAGES}
  Номер журнала: #d.at("Номер", default: "—")
  #if i < ${PREVIEW_PAGES} { pagebreak() }
]
`;

export async function ensurePreviewTemplate({ api, typeId }) {
  const existing = (await api('GET', `/templates?documentTypeId=${typeId}`))
    .find(t => t.name === PREVIEW_TEMPLATE);
  if (existing) return;
  await api('POST', '/templates', { documentTypeId: typeId, name: PREVIEW_TEMPLATE, content: CONTENT });
  console.log(`  + шаблон «${PREVIEW_TEMPLATE}»`);
}
