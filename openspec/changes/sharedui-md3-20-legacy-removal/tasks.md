# Tasks: финал перехода на MD3

## 0. До кода

- [ ] 0.1 Предусловия сверены: `sharedui-md3-17-screens` слит и заархивирован; уровни `legacy-class`,
  `legacy-radius`, `legacy-shadow`, `legacy-type`, `notice-plaque`, `hand-card` в `ui-baseline.json` —
  пусты. Срок отмечен в зонтике: PR открыт не позже двух недель после архива 17 (зонтик, 4.2). Проверка —
  `npm run ui:ratchet`, дата архива 17 и дата PR в описании
- [ ] 0.2 Макеты не нужны: вид не меняется (зонтик, D10). Снимки ключевых экранов «до» сняты на
  master перед PR. Проверка — снимки в описании PR

## 1. Сторож словаря темы

- [ ] 1.1 `color-classes-lib.mjs` → `theme-classes-lib.mjs`: тот же токенизатор `CANDIDATE`, утилиты
  `rounded`, `text` (размер), `shadow` вместе с цветом; подсказка — ближайшая роль по
  `md3-class-map.json` [UI-82]. Проверка — `theme-classes.test.mjs` зелёный на текущем коде (ноль
  нарушений до сброса)
- [ ] 1.2 Фикстуры теста: `text-fg2`, `bg-gray-500`, `rounded-2xl`, `text-[11px]`, `shadow-lg`, класс в
  `clsx` и в карте вариантов — каждый краснит сторож, имя роли в отказе верно [UI-82]. Проверка —
  `npm test -- theme-classes` зелёный, фикстуры красные

## 2. Псевдонимы и сброс пространств имён

- [ ] 2.1 Генератор `md3-theme.mjs` больше не пишет блок псевдонимов; слой `@theme inline`
  перегенерирован; тест дрейфа палитры зелёный [UI-82]. Проверка — `md3-theme.test.mjs` зелёный, в
  сгенерированном слое нет `fg1`, `base`, `stroke`, `brand`, `tonal`, `subtle`, `danger`
- [ ] 2.2 `@theme`: `--color-*: initial`, `--radius-*: initial`, `--text-*: initial`, `--shadow-*: initial`
  (D2), затем роли и шкалы MD3 [UI-82]. Проверка — `npm run build`; в собранном CSS нет правила для
  `bg-brand` и `text-fg1`; `theme-classes.test.mjs` зелёный
- [ ] 2.3 Сторож срабатывает: во временный файл экрана вписаны `text-fg1`, `bg-gray-500`, `rounded-2xl`,
  `shadow-lg` — сторож называет каждый; файл удалён [UI-82]. Проверка — вывод в описании PR
- [ ] 2.4 Снимки ключевых экранов «после» совпадают со снимками «до» в обеих темах на 1366×768 и 375 px
  (зонтик, 5.2) [UI-82]. Проверка — пары снимков в описании PR

## 3. Правила храповика

- [ ] 3.1 Сняты `legacy-class`, `palette`, `text-size` в `ui-ratchet-lib.mjs`, их фикстуры — в
  `ui-ratchet.test.mjs`, записи — в `ui-baseline.json`; `legacy-token` остаётся [UI-82]. Проверка —
  `ui-ratchet.test.mjs` зелёный; фикстура `var(--f-shadow28)` краснит `legacy-token`
- [ ] 3.2 `CLAUDE.md` и `AGENTS.md` — перечень правил храповика интерфейса без снятых правил, со сторожем
  словаря, одним коммитом [UI-82]. Проверка — `AgentRulesCopyTests` зелёный

## 4. Документы и дизайн-система

- [ ] 4.1 `docs/UI.md` в финальном виде: «Язык формы: Material 3»; раздел «Где мы отступаем от MD3 и
  почему» — все отступления D8 с причиной и решением владельца (В5, В7, В8 и др.), без слова «MD3» рядом;
  «Кто сторожит» — сторож словаря и оставшиеся правила [UI-82]. Проверка — `rg -i "fluent|--f-" docs/UI.md`
  находит только историю перехода и `legacy-token`; `cd docs/tools && npm run toc` — оглавление верно
- [ ] 4.2 Дизайн-система «BHS.CRG UI»: README («Styling idiom») и `conventions.md` — только словарь MD3;
  `_adherence.oxlintrc.json` — на новый словарь; `/design-sync` из основной копии (`.design-sync/`
  копируется в worktree и обратно, D12) [UI-82]. Проверка — в README нет `text-fg`, `bg-base`,
  «Fluent 2 scale»; превью без lucide
- [ ] 4.3 Снимки `docs/images` (31 файл) пересняты харнессом на сиде стенда; PDF инструкций пересобран
  (`cd docs/tools && npm run pdf`). Проверка — ссылки `USER_GUIDE.md` и `ADMIN_GUIDE.md` ведут на новые
  снимки; ревью подписей

## 5. Верификация

- [ ] 5.1 Финал эпика (зонтик, 5.3): в `src/` нет `--f-`, `lucide-react`, старых имён классов; правила
  `legacy-*`, `lucide-import`, `raw-state`, `dark-variant` — на нуле или сняты. Проверка —
  `rg -- "--f-|lucide-react" src/` пуст; `npm run ui:ratchet`
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 5.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — зелёные
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~AgentRulesCopy|FullyQualifiedName~BrandIcon"`
  — зелёный (копия правил; сторож «сервер ↔ палитра» из 01)
- [ ] 5.5 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный
- [ ] 5.6 Весь `npm run -s test:e2e:ci` зелёный (зонтик, 5.1); список проверок 5.1–5.5 — в описании PR
- [ ] 5.7 PR с меткой `version:minor`
