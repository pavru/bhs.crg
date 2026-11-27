package ru.bimhouse.crg.field.entry

import android.content.Context
import androidx.room.withTransaction
import java.time.Instant
import java.time.ZoneId
import java.util.UUID

/** Снимок справочников и возвращённого отчёта для локальной приёмки MOB-06. */
internal object EntryBootstrap {
    private val companyZone = ZoneId.of("Asia/Vladivostok")
    val owner = ReportOwner("mob06-debug-fixtures", id(1))
    private val site = EntrySite(
        id(2), "ЖК «Восточный»", companyZone,
        listOf(EntryChoice(key(10), "К1"), EntryChoice(key(11), "К2")),
        setOf(key(30), key(31), key(32)),
        listOf(EntryChoice(key(20), "Марина Белова"), EntryChoice(key(21), "Илья Орлов")),
    )
    private val cable = EntryWork(
        key(30), "Прокладка кабеля", key(40),
        listOf(EntryChoice(key(50), "В лотке"), EntryChoice(key(51), "В трубе")),
        requiresPlace = true, requiresMaterial = true, hasWebFields = true,
    )
    private val tray = EntryWork(
        key(31), "Монтаж кабельного лотка", key(40),
        listOf(EntryChoice(key(52), "Перфорированный"), EntryChoice(key(53), "Лестничный")),
        requiresPlace = true, requiresMaterial = true,
    )

    suspend fun load(context: Context): FieldSession? {
        val profile = FieldSession(
            owner, "BHS.CRG", "Сергей Мельников", companyZone, listOf(site),
            listOf(cable, tray, EntryWork(key(32), "Маркировка линий", key(41)), EntryWork(key(33), "Проверка изоляции", key(42))),
            listOf(EntryChoice(key(40), "м"), EntryChoice(key(41), "шт."), EntryChoice(key(42), "линия")),
        )
        val db = OfflineDatabase.open(context, owner)
        try {
            db.withTransaction {
                val dao = db.dao()
                if (dao.report(key(3)) == null) {
                    val now = Instant.now()
                    val date = now.atZone(companyZone).toLocalDate().minusDays(1)
                    dao.insert(ReportRecord(
                        id = key(3), constructionId = site.id.toString(), unlistedSiteName = null,
                        companyZoneId = companyZone.id, siteZoneId = site.zone?.id, windowZoneId = companyZone.id,
                        date = date.toString(), shift = Shift.NIGHT,
                        windowEndsAt = SubmissionWindow.endsAt(companyZone, date, Shift.NIGHT).toString(),
                        hours = "8", note = null, status = ReportStatus.RETURNED,
                        submittedAt = now.minusSeconds(3600).toString(), revision = 1,
                        returnNote = "Уточните этаж и оси в строке «Прокладка кабеля». В К2 место уже указано.",
                        returnedAt = now.toString(),
                    ))
                    val cableLine = WorkLine(
                        id = key(4), workTypeId = cable.id, unitId = cable.unitId, variantId = key(50),
                        volume = "42", sectionId = key(10), materials = listOf(WorkMaterial(null, "ВВГнг 3×2,5")),
                    )
                    val trayLine = WorkLine(
                        id = key(5), workTypeId = tray.id, unitId = tray.unitId, variantId = key(52),
                        volume = "18", sectionId = key(11), location = "2 этаж",
                        materials = listOf(WorkMaterial(null, "Лоток 100×50")),
                    )
                    dao.insertLines(listOf(ReportLineRecord(key(3), 0, cableLine), ReportLineRecord(key(3), 1, trayLine)))
                }
            }
        } finally {
            db.close()
        }
        return profile
    }

    private fun id(value: Long) = UUID(0x2142, value)
    private fun key(value: Long) = id(value).toString()
}
