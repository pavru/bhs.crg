# BHS.CRG для Android

Для сборки нужны JDK 17 и Android SDK Platform 36. Путь к SDK задаётся через
`ANDROID_HOME` или `sdk.dir` в локальном `local.properties`.

Команды из `src/mobile`:

```sh
./gradlew :app:assembleDebug :app:testDebugUnitTest
./gradlew :app:connectedDebugAndroidTest
```

В Windows используйте `gradlew.bat`. Вторая команда запускает проверки Room на
подключённом устройстве или эмуляторе. JSON примеров JVM-тесты читают из проекта
`BHS.CRG.Modules.Work`. Экспорт схем Room хранится в `app/schemas`.

## Приёмка офлайн-ввода MOB-06

Debug-сборка открывает демонстрационный профиль и возвращённую ночную смену.
Сценарий Compose проверяет правку строки, поворот с открытой формой, повторную подачу,
правку объёма с сохранением варианта, новую смену без стройки и очередь Room.
Перед прогоном включите авиарежим и отключите Wi-Fi на выбранном устройстве.

```sh
ANDROID_SERIAL=SERIAL ./gradlew :app:connectedDebugAndroidTest -Pandroid.testInstrumentationRunnerArguments.requireAirplane=true
adb -s SERIAL shell am force-stop ru.bimhouse.crg.field
adb -s SERIAL shell am instrument -w -e class ru.bimhouse.crg.field.entry.OfflineEntryUiTest -e afterProcessRestart true ru.bimhouse.crg.field.test/androidx.test.runner.AndroidJUnitRunner
```

Для PowerShell установите `$env:ANDROID_SERIAL` и заключите аргумент `-P…=true` в кавычки.
Отдельный запуск после остановки процесса проверяет уже сохранённые отчёты и очередь.
