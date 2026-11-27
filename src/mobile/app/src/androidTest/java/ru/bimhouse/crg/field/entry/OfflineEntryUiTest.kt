package ru.bimhouse.crg.field.entry

import android.content.pm.ActivityInfo
import android.provider.Settings
import androidx.compose.ui.test.assert
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTextReplacement
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.util.UUID

class OfflineEntryUiTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val arguments = InstrumentationRegistry.getArguments()

    @get:Rule
    val compose = createEmptyComposeRule()

    @Test
    fun offlineReturnAndUnlistedEntryWithTapCounts() {
        if (arguments.getString("afterProcessRestart") == "true") {
            afterProcessRestartReadsRoomInAirplaneMode()
            return
        }
        if (arguments.getString("requireAirplane") == "true") assertAirplane()
        context.deleteDatabase(EntryBootstrap.owner.databaseName())
        ActivityScenario.launch(EntryActivity::class.java).use { scenario ->
            awaitTag("continue-night")
            compose.onNodeWithTag("continue-night").performScrollTo().performClick()
            if (arguments.getString("requireAirplane") == "true") {
                compose.onNodeWithTag("connection-state").assert(hasText("Нет связи"))
            }
            tapTag("return-comment-toggle")
            see("Свернуть")
            tap("Изменить · Прокладка кабеля")
            compose.onNodeWithTag("line-variant").assert(hasText("Вариант вида работы · В лотке"))
            input("line-place", "5 этаж, оси 2–4")
            scenario.onActivity { it.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE }
            awaitTag("report-panel")
            compose.onNodeWithTag("line-place").assert(hasText("5 этаж, оси 2–4"))
            tapTag("line-next")
            tap("Указать бригаду")
            tap("Марина Белова")
            tapTag("line-save")
            awaitText("Подать снова")
            tapTag("submit-report")
            awaitText("Отчёт ожидает доставки.")
            seeSubmitted()
            compose.onAllNodes(hasContentDescription("На телефоне"), useUnmergedTree = true)
                .fetchSemanticsNodes().let { assertTrue(it.isNotEmpty()) }
            tap("Изменить · Прокладка кабеля")
            compose.onNodeWithTag("line-volume").performScrollTo().performTextReplacement("43")
            tapTag("line-next")
            tapTag("line-save")
            awaitText("43 м")
            tap("Новый отчёт")
            compose.onNodeWithTag("select-Стройка").performClick()
            compose.onNodeWithText("Объекта нет в списке").performClick()
            input("unlisted-name", "Подстанция на Седанке", scroll = false)
            compose.onNodeWithText("Открыть отчёт").performClick()
            awaitText("Выбрать работу")
            val counts = compareEntryTaps()
            println("MOB-06 taps: concrete=${counts.first}, arbitrary=${counts.second}")
            assertEquals(6 to 8, counts)
            input("report-hours", "2")
            tapTag("submit-report")
            awaitText("Отчёт ожидает доставки.")
        }
        verifyRoom()
    }

    private fun afterProcessRestartReadsRoomInAirplaneMode() {
        assertAirplane()
        ActivityScenario.launch(EntryActivity::class.java).use {
            awaitTag("report-list")
            tapTag("report-row-${UUID(0x2142, 3)}")
            compose.onNodeWithTag("connection-state").assert(hasText("Нет связи"))
            awaitText("Отчёт ожидает доставки.")
            seeSubmitted()
        }
        verifyRoom()
    }

    private fun compareEntryTaps(): Pair<Int, Int> {
        var taps = 0
        fun press(text: String) { tap(text); taps++ }
        fun field(tag: String, text: String) { input(tag, text); taps++ }
        fun dropdown(text: String) { compose.onNodeWithText(text).performClick(); taps++ }

        press("Выбрать работу")
        press("Все работы")
        press("Маркировка линий")
        field("line-volume", "4")
        press("Далее")
        press("Сохранить работу")
        awaitText("Выбрать работу")
        val concrete = taps

        taps = 0
        press("Выбрать работу")
        press("Своя работа")
        field("custom-work-name", "Обход кабельной трассы")
        tapTag("line-unit")
        taps++
        dropdown("шт.")
        field("line-volume", "2")
        press("Далее")
        press("Сохранить работу")
        awaitText("Выбрать работу")
        return concrete to taps
    }

    private fun verifyRoom() = runBlocking {
        OfflineReportStore.open(context, EntryBootstrap.owner).use { store ->
            val returned = requireNotNull(store.report(UUID(0x2142, 3)))
            assertEquals(ReportStatus.SUBMITTED, returned.report.status)
            assertEquals(Shift.NIGHT, returned.report.shift)
            assertEquals("8", returned.report.hours)
            assertTrue(returned.report.returnNote.orEmpty().contains("Уточните этаж"))
            val repaired = returned.lines.first()
            assertEquals(UUID(0x2142, 4).toString(), repaired.id)
            assertEquals(UUID(0x2142, 50).toString(), repaired.variantId)
            assertEquals("43", repaired.volume)
            assertEquals("5 этаж, оси 2–4", repaired.location)
            assertEquals(listOf(UUID(0x2142, 20).toString()), repaired.participants)
            val unlisted = store.reports().single { it.report.unlistedSiteName == "Подстанция на Седанке" }
            assertEquals(ReportStatus.SUBMITTED, unlisted.report.status)
            assertEquals(Shift.DAY, unlisted.report.shift)
            assertEquals("Asia/Vladivostok", unlisted.report.windowZoneId)
            assertEquals(2, unlisted.lines.size)
            assertTrue(unlisted.lines.any { it.customName == "Обход кабельной трассы" && it.volume == "2" })
            assertEquals(setOf(returned.report.id, unlisted.report.id),
                store.pendingActions().filter { it.action == ReportAction.SUBMIT }.map { it.reportId }.toSet())
        }
    }

    private fun assertAirplane() {
        assertEquals(1, Settings.Global.getInt(context.contentResolver, Settings.Global.AIRPLANE_MODE_ON, 0))
    }
    private fun input(tag: String, value: String, scroll: Boolean = true) {
        val field = compose.onNodeWithTag(tag)
        if (scroll) field.performScrollTo()
        field.performClick().performTextInput(value)
    }
    private fun tapTag(tag: String) { compose.onNodeWithTag(tag).performScrollTo().performClick() }
    private fun tap(text: String) { compose.onNodeWithText(text).performScrollTo().performClick() }
    private fun see(text: String) { compose.onNodeWithText(text).performScrollTo().assertIsDisplayed() }
    private fun seeSubmitted() { compose.onNodeWithTag("life-status").performScrollTo().assert(hasText("Подан")) }
    private fun awaitText(text: String) { compose.waitUntil(10_000) { compose.onAllNodes(hasText(text)).fetchSemanticsNodes().isNotEmpty() } }
    private fun awaitTag(tag: String) { compose.waitUntil(10_000) { compose.onAllNodes(androidx.compose.ui.test.hasTestTag(tag)).fetchSemanticsNodes().isNotEmpty() } }
}
