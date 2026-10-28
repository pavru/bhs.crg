# Tasks: перевод классов перезапускаемым кодмодом

Один PR после слияния `sharedui-md3-01-tokens`, без метки `version:minor`. Сливается сразу после
зелёного CI.

## 1. Карта и скрипт

- [x] 1.1 `src/client/scripts/md3-class-map.json`: разделы `names` (с `same: true`), `classes` (с `skip`
  для `text-stroke-strong`, `text-base`), `vars` (`--f-*` и `--color-<старое>`) по таблице `design.md`
  [UI-58]. Проверка — тест пар 1.3 зелёный
- [x] 1.2 `src/client/scripts/md3-migrate.mjs` и `npm run md3:migrate` (+ `--check`): токенизатор
  `CANDIDATE` с вариантами, `/opacity`, `!`; переменные; один проход по исходному тексту; проходы кода и
  комментариев; отрицательные ожидания не трогаются; отчёт пар, файлов и неоднозначного с `файл:строка`
  [UI-58]. Проверка — `scripts/md3-migrate.test.mjs`: фикстуры вариантов, строки вне `className`,
  комментария, `text-base`, `text-brand-hover`, `.not.toContain`, цепной карты
- [x] 1.3 Тест пар в `md3-migrate.test.mjs`: каждая пара `same: true` × цветовые утилиты — одинаковые
  объявления с разрешёнными переменными в обеих темах [UI-58]. Проверка — зелёный; пара
  `fg3 → on-surface-variant` с `same: true` делает его красным с названием пары и темы
- [x] 1.4 Идемпотентность: второй прогон по дереву после первого — «замен 0», `--check` — код 0 [UI-58].
  Проверка — тест на фикстурах и ручной прогон по `src/` в PR

## 2. Прогон по `src/`

- [x] 2.1 `npm run md3:migrate` по всему `src/client/src`; отчёт (пары, число файлов, неоднозначное) — в
  описании PR [UI-58]. Проверка — `color-classes.test.mjs` зелёный (все классы существуют); `npx tsc -b`
  зелёный; число строк файлов не изменилось (`git diff --stat` — только замены)
- [x] 2.2 Отрицательные ожидания из отчёта (~10) переписаны руками на роли MD3 [UI-58]. Проверка — для
  каждого: временная вставка запрещённого класса делает тест красным; список — в описании PR
- [x] 2.3 Фикстуры `ui-ratchet.test.mjs` и `color-classes.test.mjs` — на ролях MD3, одна фикстура старого
  имени у `white-on-fill` и `legacy-class` остаётся [UI-58]. Проверка — тесты зелёные, `bg-primary
  text-white` и `bg-brand text-white` красные
- [x] 2.4 Уровень `legacy-class` опущен `npm run ui:ratchet:update` [UI-58]. Проверка — в уровне только
  файлы с неоднозначными именами из отчёта 2.1

## 3. Чужая ветка и документы

- [x] 3.1 Проверка на живой ветке: ветка с правками `features/` (любая открытая `sharedui-*` или временная
  с новым файлом на `text-fg2 bg-base shadow-[var(--f-shadow16)]`) — «master до перевода, перевод
  с версией ветки в конфликтах (`-X ours`) + `npm run md3:migrate`» [UI-58]; версия master в конфликтах
  теряет правку ветки в той же строке — это показано тем же прогоном. Проверка — после команды `npx tsc -b`, `npm test`, `npm run ui:ratchet`
  зелёные; итог — в описании PR
- [x] 3.2 `docs/UI.md`: раздел «Перевод: как обновить ветку» (три шага, `--check`), «Цвет» — примеры
  только ролями; «Кто сторожит» — тест карты [UI-58]. Проверка — в примерах `docs/UI.md` нет старых имён,
  кроме колонки псевдонимов
- [ ] 3.3 После слияния — `/design-sync` из основной копии: бандл дизайн-системы собран из `shared/ui` и
  нёс старые классы. Проверка — в `_ds_bundle` нет `text-fg1`, `bg-base`

## 4. Верификация

- [x] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~AgentRulesCopy"` — зелёный (сервер не меняется; проверка на случай правки документов агентов)
- [x] 4.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [x] 4.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста, `legacy-class` ниже
  прежнего уровня
- [x] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 4.5 `cd src/client && npm run -s test:e2e:ci` — весь список живых прогонов зелёный: правка общих
  компонентов `shared/ui` (зонтик, 5.1)
