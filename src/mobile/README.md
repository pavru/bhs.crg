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
