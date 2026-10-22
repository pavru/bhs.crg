# Tasks: предпросмотр документа без прыжка

Условие — `viewer-01-component` слит. Макеты не нужны: вид панели не меняется, кнопки переводятся по
готовому правилу UI.md.

## 1. Подмена в просмотрщике

- [ ] 1.1 Тот же `documentKey` и новые байты:
  - фоновая загрузка, предыдущая гасится `loadingTask.destroy()`;
  - якорь снимается в момент готовности, подмена одним кадром;
  - `destroy()` прежнего ровно раз;
  - якорь прижимается к последнему листу.
  [UI-25]. Проверка — тест компонента с подменённым движком:
  - лента ни разу не пустая;
  - позиция, сменённая во время загрузки, сохранена;
  - две загрузки подряд — показана вторая, первая погашена;
  - `destroy` старого вызван ровно раз

## 2. Панель предпросмотра

- [ ] 2.1 `previewDocument` → `{ kind: 'pdf', blob }`. `DocumentPreviewPanel` на `PdfViewer`:
  `documentKey = instanceId`, `rememberZoomAs="document-preview"`, без `urlRef` и отзыва. Многострочный
  отказ компилятора сохранён. Проверка — `npx tsc -b`; `DocumentPreviewPanel.tsx` ушёл из уровня
  `native-pdf-frame`
- [ ] 2.2 «Повторить» → `Button variant="text"`. Кнопки шапки → `IconButton size="sm"`, только если
  `sharedui-icon-button-screens` (1.2) ещё не слит; тогда пункт вычеркнуть там. Проверка —
  `npm run ui:ratchet` без роста по `icon-button`
- [ ] 2.3 Живой прогон предпросмотра (D «Живой прогон» в `design.md`): `MutationObserver` — `canvas` ни
  разу не 0; после правки реквизита номер страницы прежний. При необходимости посев получает шаблон по
  умолчанию на 3–5 листов; новый файл прогона записан в `run-suites.mjs`. Проверка — живой прогон
  зелёный в CI, `suites.test.mjs` зелёный

## 3. Открытие готового PDF

- [ ] 3.1 `previewGeneratedFile` → `openInNewTab` с загрузчиком: окно сразу, отзыв с отсрочкой; плейсхолдер
  и текст отказа — в общей функции. Проверка — тест функции: `revokeObjectURL` вызван после отсрочки; в
  `documentSets.ts` нет «intentionally not revoking»

## 4. Приёмка

- [ ] 4.1 Ручной замер:
  - документ на 200+ листов, 10 перегенераций — DevTools → Memory вернулся к прежнему уровню;
  - правка реквизита на пятой странице — видна пятая.
  Проверка — описание PR

## 5. Верификация

- [ ] 5.1 `cd src/client && npm test` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста, уровень `native-pdf-frame` опущен
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный

## Workflow follow-up

- PR: метки `version:minor` и `ui`; «Closes #1493».
