# Tasks: подсказка материалов строки «отпущено → смета → справочник»

## 1. Адрес подсказки

- [ ] 1.1 `GET /api/work/materials/hint` за воротами модуля под `work.report.own` или
  `work.report.review`; стройка — по разрешению доступа спрашивающего [WORK-39] [AUTH-8]. Проверка —
  интеграционные тесты: чужая стройка — 404, без права — 403, модуль выключен — 501
- [ ] 1.2 Ярус `issued` — контракт «Материалы на объекте» (`work-09`) с параметром доступа; без количеств
  и накладных [WORK-5.1] [WORK-48] [COST-18]. Проверка — тест сверяет ключи JSON яруса; живой хост
  `work` без `costs` даёт `not-connected`
- [ ] 1.3 Ярус `estimate` — контракт «Плановые работы» по позиции перечня строки, пометка `notIssued`
  [WORK-5.1] [PLAN-9.1]. Проверка — тесты состояний `not-connected`, `hidden` с кодом права, `no-item`
- [ ] 1.4 Ярус `catalog` — `IModuleCatalog.SearchAsync`: `inArchive`, `more`, причина «справочник не
  заведён» при `null` [WORK-5.1] [CORE-34.4]. Проверка — тест: отсечённый список несёт `more: true`
- [ ] 1.5 Снятие повторов сверху вниз [WORK-5.1]. Проверка — тест: позиция из `issued` не повторяется в
  `estimate` и `catalog`

## 2. Клиент

- [ ] 2.1 Хук подсказки в `src/client/src/shared/api/` и `src/client/src/shared/ui/MaterialPicker.tsx`
  по выбранному макету Р3: ярусы с подписями, «не отпущен», состояния — строкой с причиной [WORK-5.1].
  Проверка — vitest: `not-connected` и `hidden` не рисуются пустым блоком; пометка берётся из
  `notIssued`, клиент её не вычисляет
- [ ] 2.2 Форма строки веб-ввода (`work-22`) берёт `MaterialPicker` вместо выбора из справочника
  целиком [WORK-5.1]. Проверка — `cd src/client && npx tsc -b`; vitest формы строки зелёный
- [ ] 2.3 `docs/USER_GUIDE.md` — порядок подсказки и пометка «не отпущен»; содержание — `npm run toc` в
  `docs/tools`. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkMaterialsHint|FullyQualifiedName~WorkOnlyHost|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 Живой прогон под монтажником: `work-smoke.mjs` (`work-25`) дополнен проверкой ярусов
  подсказки — зелёный
