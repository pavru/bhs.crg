package ru.bimhouse.crg.field.entry

import android.content.Context

/**
 * Профиль появляется при подключении устройства (MOB-07).
 * Выпуску пока доступен экран ожидания подключения.
 */
internal object EntryBootstrap {
    suspend fun load(context: Context): FieldSession? = null
}
