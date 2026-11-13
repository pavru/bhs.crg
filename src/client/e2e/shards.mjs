// Раскладка живых прогонов CI по долям: каждая доля идёт своей работой, на своём раннере и своём
// стенде (своя база, своё хранилище, свой сервер).
//
// Почему не одновременно на одном стенде. Все наборы входят одним админом и правят общее: снимают
// с него роли (`routing`), переносят тэг в схеме справочника организаций (`field-mapping`), правят
// схему счёта, сбрасывают профили печати, пишут настройки учётной записи. Уборка всё возвращает,
// но в промежутке соседний набор увидел бы чужое состояние. Подробности и замер —
// docs/DEV_NOTES.md, «Живые прогоны: три доли».
//
// Состав прогона CI по-прежнему один — аргументы `test:e2e:ci` в package.json; доля берёт из него
// своё. Внутри доли наборы идут в порядке этого списка: раскладка решает только, ГДЕ набор идёт,
// а не в каком порядке.

/**
 * Вес набора — секунды в CI, от строки «──────── имя ────────» до следующей (прогон 43366209391,
 * 13.11.2026, более долгий из двух замеренных). Точность не нужна: вес решает только, в какую долю
 * лечь набору. Новый набор без веса — отказ сторожа `suites.test.mjs` с просьбой вписать.
 */
export const SUITE_SECONDS = {
  harness: 16,
  keyboard: 9,
  routing: 30,
  types: 40,
  settings: 39,
  'shared-ui': 10,
  pages: 9,
  fields: 14,
  dialogs: 14,
  limited: 5,
  invoices: 159,
  'invoice-lines': 109,
  'allocation-matrix': 40,
  'invoice-payment': 38,
  'site-costs': 15,
  costs: 36,
  'table-chips': 11,
  'table-view': 35,
  'table-saved-views': 15,
  'field-mapping': 8,
  'planning-estimates': 10,
};

/**
 * Разложить наборы на `count` долей. Самый тяжёлый набор кладётся в самую лёгкую на этот момент
 * долю; равные веса — в порядке списка, равные доли — младшая первой. Раскладка детерминирована:
 * одна и та же на каждом раннере, иначе доли посчитали бы её по-разному и набор потерялся бы.
 *
 * @param {string[]} names наборы в порядке прогона
 * @param {Record<string, number>} seconds веса
 * @param {number} count число долей
 * @returns {string[][]} наборы каждой доли — в порядке `names`
 */
export function splitIntoShards(names, seconds, count) {
  if (!Number.isInteger(count) || count < 1) throw new Error(`число долей — целое от 1: ${count}`);
  const missing = names.filter(name => !(name in seconds));
  if (missing.length) throw new Error(`у наборов нет веса: ${missing.join(', ')} — впишите в e2e/shards.mjs`);

  const loads = Array.from({ length: count }, () => 0);
  const shardOf = new Map();
  const heaviestFirst = names
    .map((name, index) => ({ name, index }))
    .sort((a, b) => seconds[b.name] - seconds[a.name] || a.index - b.index);
  for (const { name } of heaviestFirst) {
    const lightest = loads.indexOf(Math.min(...loads));
    shardOf.set(name, lightest);
    loads[lightest] += seconds[name];
  }
  return loads.map((_, shard) => names.filter(name => shardOf.get(name) === shard));
}

/**
 * Разобрать `--shard K/N` (или `--shard=K/N`). Нет ключа — `null`: гонять весь список.
 * Битый ключ — исключение: молча прогнать весь список вместо доли значило бы гонять каждый набор
 * трижды и выдать это за успех.
 *
 * @param {string[]} args аргументы командной строки
 * @returns {{ shard: { index: number, count: number } | null, rest: string[] }}
 */
export function parseShardArg(args) {
  const rest = [];
  let value = null;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--shard') { value = args[++i] ?? ''; continue; }
    if (arg.startsWith('--shard=')) { value = arg.slice('--shard='.length); continue; }
    rest.push(arg);
  }
  if (value === null) return { shard: null, rest };
  const match = /^(\d+)\/(\d+)$/.exec(value);
  const index = match ? Number(match[1]) : NaN;
  const count = match ? Number(match[2]) : NaN;
  if (!match || count < 1 || index < 1 || index > count) {
    throw new Error(`--shard ждёт «доля/всего», например 2/3, а получено «${value}»`);
  }
  return { shard: { index, count }, rest };
}
