# Tasks

## 1. Очередь [UI-71]

- [ ] 1.1 `snackbarQueue.ts`: поле `dropped` в состоянии и действие `flush`. `next` (устаревшие) и `push`
  (переполнение) кладут туда отброшенных с `onClose`, а уведомление с `onClose` минует ключ повтора (D2,
  D4). Проверка — `snackbarQueue.test.ts`: устаревшее и вытесненное с откликом в `dropped`, без отклика — нет;
  повтор с откликом ставится; `flush` очищает.

## 2. Тип и провайдер [UI-71]

- [ ] 2.1 `Snackbar.ts`: `SnackbarCloseReason`, `SnackbarOptions.onClose`, `SNACKBAR_UNDO_MS = 8000` с
  доккомментариями. Проверка — `npx tsc -b`.
- [ ] 2.2 `SnackbarProvider.tsx`: причина в ссылке (`action`, `dismissed` по `Close`, смахиванию и Esc;
  иначе `timeout`), эффект `dropped` → `flush`, один вызов на `id` (D1, D2). Проверка —
  `SnackbarProvider.test.tsx`: четыре причины по сценариям спецификации, Esc и «Закрыть» дают `dismissed`,
  пауза на наведении откладывает `timeout`, отклик не зовётся дважды.

## 3. Потребители и документы [UI-71]

- [ ] 3.1 `ArticlesDialog.tsx`, `useRecordArchive.ts`, `useToggleFavorite.ts`: срок «Вернуть» —
  `SNACKBAR_UNDO_MS`. Проверка — поиск `duration: 8000` рядом с «Вернуть» пуст, тесты этих мест зелёные.
- [ ] 3.2 `docs/UI.md`: в строке «Уведомление» и абзаце под таблицей — отклик закрытия с причиной,
  отброшенное считается закрытым, «Вернуть» 8 с. Проверка — текст совпадает со спецификацией.

## 4. Верификация

- [ ] 4.1 `cd src/client && npx tsc -b`, `npm test`, `npm run lint:ratchet`, `npm run ui:ratchet` —
  зелёные.
- [ ] 4.2 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — файлы
  не перешагнули порог.

## Workflow follow-up

- Код слит отдельным PR; после коммита бота с версией — архив `/opsx:archive` отдельным PR и отметка 2.2a
  в `costs-invoice-screen-00-epic/tasks.md`.
- 12 и 13 переносят решение к себе своими задачами 0.1a — не здесь.
