import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Каждый живой прогон обязан гоняться в CI (issue #975).
 *
 * Зачем сторож. Файл `*-smoke.mjs` в этой папке — работа, которую кто-то сделал и проверил руками;
 * состав же прогона CI перечислен ОТДЕЛЬНО, аргументами `test:e2e:ci`. Забыть дописать имя ничего
 * не ломает: прогон просто не идёт, а папка при этом выглядит полной, и следующий читатель уверен,
 * что проверка стоит на посту. Именно так и теряется вся ценность прогона — молча.
 *
 * Сверяется текст `package.json`, а не поведение: спросить у CI, что он запустил, из юнит-теста
 * нельзя, а список — это и есть то место, где ошибаются.
 *
 * Осознанно исключить прогон из CI можно — впиши его в `NOT_IN_CI` вместе с причиной. Отказ теста
 * тогда превращается в заявление, а не в забывчивость.
 */
const here = path.dirname(fileURLToPath(import.meta.url));

/** Прогоны, которых в CI нет НАРОЧНО: имя → причина. Пусто — значит гоняются все. */
const NOT_IN_CI = {};

describe('состав живых прогонов в CI', () => {
  const pkg = JSON.parse(readFileSync(path.join(here, '..', 'package.json'), 'utf8'));
  const suites = readdirSync(here)
    .filter(f => f.endsWith('-smoke.mjs'))
    .map(f => f.replace('-smoke.mjs', ''));
  const inCi = (pkg.scripts['test:e2e:ci'] ?? '').split(/\s+/).slice(2);

  it('нашлись сами файлы прогонов', () => {
    // Иначе всё ниже проходит на пустом списке и не значит ничего.
    expect(suites.length).toBeGreaterThan(5);
  });

  it.each(suites)('прогон %s гоняется в CI', name => {
    if (name in NOT_IN_CI) {
      expect(inCi, `прогон «${name}» объявлен исключённым (${NOT_IN_CI[name]}), но стоит в списке CI`)
        .not.toContain(name);
      return;
    }
    expect(inCi,
      `прогон «${name}» есть файлом, но не гоняется в CI: допишите его в аргументы test:e2e:ci `
      + '(или в NOT_IN_CI с причиной — молчаливого пропуска быть не должно)')
      .toContain(name);
  });

  it.each(suites)('у прогона %s есть свой скрипт запуска', name => {
    // Отдельный скрипт — то, чем прогон запускают по одному при починке. Без него имя приходится
    // вспоминать, а команду набирать руками, и прогон перестают гонять локально.
    expect(pkg.scripts, `нет скрипта test:e2e:${name}`).toHaveProperty(`test:e2e:${name}`);
  });

  it('в списке CI нет имён без файла', () => {
    // Обратная половина: опечатку в имени `run-suites` ловит сам — «не запустился» и красный итог.
    // Но ловит он её в CI, после подъёма всего приложения: минут десять ради буквы. Здесь то же
    // расхождение называется за секунду и по имени.
    expect(inCi.filter(n => !suites.includes(n))).toEqual([]);
  });
});
