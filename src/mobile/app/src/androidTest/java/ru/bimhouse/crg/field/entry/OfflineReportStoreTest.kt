package ru.bimhouse.crg.field.entry

import android.database.sqlite.SQLiteConstraintException
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class OfflineReportStoreTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val owner = ReportOwner("instance-test", UUID.randomUUID())
    private val owners = mutableListOf(owner)
    private val now = Instant.parse("2026-11-16T16:00:00Z")
    private val date = LocalDate.parse("2026-11-16")
    private val vladivostok = ZoneId.of("Asia/Vladivostok")
    private val moscow = ZoneId.of("Europe/Moscow")
    private val unlisted = ReportPlacement(vladivostok, unlistedSiteName = "Новый объект")
    private val line = WorkLine(id = UUID.randomUUID().toString(), variantId = UUID.randomUUID().toString())

    @After
    fun cleanUp() {
        owners.forEach { context.deleteDatabase(it.databaseName()) }
    }

    @Test
    fun unlistedNightAndActionsSurviveReopenAndBinding() = runBlocking {
        val reportId = UUID.randomUUID()
        val create = action()
        val edit = action()
        val submit = action(Instant.parse("2026-11-17T23:00:00Z"))
        val input = ReportInput(unlisted, date, Shift.NIGHT, hours = "8", lines = listOf(line))
        val editedLine = line.copy(volume = "2.50")
        OfflineReportStore.open(context, owner).use { store ->
            val saved = store.create(reportId, input, create)
            assertEquals(date, store.suggested(unlisted, now).date)
            assertEquals(Shift.NIGHT, store.suggested(unlisted, now).shift)
            val edited = store.edit(reportId, saved.report.revision, input.copy(lines = listOf(editedLine)), edit)
            store.submit(reportId, edited.report.revision, submit)
        }

        OfflineReportStore.open(context, owner).use { store ->
            val submitted = requireNotNull(store.report(reportId))
            assertEquals(ReportStatus.SUBMITTED, submitted.report.status)
            assertEquals("8", submitted.report.hours)
            assertEquals(listOf(editedLine), submitted.lines)
            assertEquals("2026-11-17T14:00:00Z", submitted.report.submittedAt)
            assertEquals(submitted, store.submit(reportId, 2, submit))
            val conflict = runCatching {
                store.edit(reportId, submitted.report.revision, input.copy(shift = Shift.DAY), action())
            }
            assertTrue(conflict.exceptionOrNull() is IllegalStateException)
            assertEquals(submitted, store.report(reportId))

            val site = UUID.randomUUID()
            val bound = store.applyConfirmedSite(reportId, site, moscow)
            assertEquals(site.toString(), bound.report.constructionId)
            assertEquals(moscow.id, bound.report.siteZoneId)
            assertEquals(date.toString(), bound.report.date)
            assertEquals(Shift.NIGHT, bound.report.shift)
            assertEquals(vladivostok.id, bound.report.windowZoneId)
            assertEquals(submitted.report.windowEndsAt, bound.report.windowEndsAt)
            assertEquals(submitted.report.submittedAt, bound.report.submittedAt)
            assertEquals(submitted.lines, bound.lines)
            assertEquals(bound, store.applyConfirmedSite(reportId, site, moscow))

            val pending = store.pendingActions()
            assertEquals(listOf(create.id, edit.id, submit.id).map(UUID::toString), pending.map { it.actionId })
            assertEquals(listOf(ReportAction.CREATE, ReportAction.EDIT, ReportAction.SUBMIT), pending.map { it.action })
            assertEquals(listOf(line), pending.first().snapshot().lines)
            assertEquals(submitted, pending.last().snapshot())
        }
    }

    @Test
    fun suggestsYesterdayNightOnlyUntilStoredDeadlineAfterZoneChange() = runBlocking {
        val placement = ReportPlacement(moscow, constructionId = UUID.randomUUID(), siteZone = vladivostok)
        val updatedPlacement = placement.copy(siteZone = moscow)
        val storedDeadline = Instant.parse("2026-11-17T14:00:00Z")

        OfflineReportStore.open(context, owner).use { store ->
            val saved = store.create(UUID.randomUUID(), ReportInput(placement, date, Shift.NIGHT), action())
            assertEquals(storedDeadline, Instant.parse(saved.report.windowEndsAt))

            assertEquals(
                SuggestedShift(date, Shift.NIGHT, "Ночная смена 16.11"),
                store.suggested(updatedPlacement, storedDeadline.minusSeconds(1)),
            )
            assertEquals(
                SuggestedShift(date.plusDays(1), Shift.DAY, "Отчёт 17.11"),
                store.suggested(updatedPlacement, storedDeadline),
            )
        }
    }

    @Test
    fun queueFailureRollsBackReportAndRows() = runBlocking {
        val reportId = UUID.randomUUID()
        val input = ReportInput(unlisted, date, lines = listOf(line))
        val database = OfflineDatabase.open(context, owner)
        OfflineReportStore(database).use { store ->
            val saved = store.create(reportId, input, action())
            assertEquals(Shift.DAY, saved.report.shift)
            val initialQueue = store.pendingActions()
            database.openHelper.writableDatabase.execSQL("""
                CREATE TRIGGER reject_queue_insert BEFORE INSERT ON report_outbox
                BEGIN SELECT RAISE(ABORT, 'simulated write failure'); END
            """.trimIndent())

            val refused = runCatching {
                store.edit(reportId, saved.report.revision, input.copy(hours = "9", lines = emptyList()), action())
            }
            assertTrue(refused.exceptionOrNull() is SQLiteConstraintException)
            assertEquals(saved, store.report(reportId))
            assertEquals(initialQueue, store.pendingActions())
        }
    }

    @Test
    fun reportAndQueueBelongToInstanceAndEmployeeTogether() = runBlocking {
        val otherEmployee = owner.copy(employeeId = UUID.randomUUID())
        val otherInstance = owner.copy(instanceKey = "other-instance")
        owners += listOf(otherEmployee, otherInstance)
        val id = UUID.randomUUID()
        val action = action()
        val input = ReportInput(unlisted, date, lines = listOf(line))
        OfflineReportStore.open(context, owner).use { it.create(id, input, action) }

        for (other in listOf(otherEmployee, otherInstance)) {
            OfflineReportStore.open(context, other).use { store ->
                assertNull(store.report(id))
                assertTrue(store.pendingActions().isEmpty())
                store.create(id, input.copy(lines = emptyList()), action)
                assertTrue(store.pendingActions().single().snapshot().lines.isEmpty())
            }
        }
        OfflineReportStore.open(context, owner).use {
            assertFalse(it.pendingActions().single().snapshot().lines.isEmpty())
        }
    }

    private fun action(deviceTime: Instant = now) = LocalAction(UUID.randomUUID(), deviceTime)
}
