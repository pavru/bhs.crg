package ru.bimhouse.crg.field.entry

import java.time.ZoneId
import java.util.UUID

internal data class EntryChoice(val id: String, val name: String)

internal data class EntryWork(
    val id: String,
    val name: String,
    val unitId: String,
    val variants: List<EntryChoice> = emptyList(),
    val requiresPlace: Boolean = false,
    val requiresMaterial: Boolean = false,
    val hasWebFields: Boolean = false,
)

internal data class EntrySite(
    val id: UUID,
    val name: String,
    val zone: ZoneId?,
    val sections: List<EntryChoice>,
    val works: Set<String>,
    val crew: List<EntryChoice>,
)

internal data class FieldSession(
    val owner: ReportOwner,
    val productName: String,
    val employeeName: String,
    val companyZone: ZoneId,
    val sites: List<EntrySite>,
    val works: List<EntryWork>,
    val units: List<EntryChoice>,
) {
    fun site(id: String?): EntrySite? = sites.find { it.id.toString() == id }
    fun work(id: String?): EntryWork? = works.find { it.id == id }
    fun unit(id: String?): String? = units.find { it.id == id }?.name
    fun siteName(record: ReportRecord): String =
        site(record.constructionId)?.name ?: record.unlistedSiteName ?: "Стройка из сохранённого отчёта"

    fun lineName(line: WorkLine): String = work(line.workTypeId)?.name ?: line.customName ?: "Сохранённая работа"

    fun missing(line: WorkLine): List<String> = buildList {
        val kind = work(line.workTypeId)
        if (line.workTypeId == null && line.customName.isNullOrBlank()) add("Название работы")
        if (line.unitId.isNullOrBlank()) add("Единица")
        if (line.volume.decimalOrNull()?.let { it.signum() > 0 } != true) add("Объём")
        if (kind != null && kind.variants.isNotEmpty() && kind.variants.none { it.id == line.variantId }) add("Вариант вида работы")
        if (kind?.requiresPlace == true && line.location.isNullOrBlank()) add("Место")
        if (kind?.requiresMaterial == true && line.materials.none { it.nomenclatureId != null || !it.note.isNullOrBlank() }) add("Материалы")
    }
}

internal fun String?.decimalOrNull() = this?.trim()?.replace(',', '.')?.toBigDecimalOrNull()
