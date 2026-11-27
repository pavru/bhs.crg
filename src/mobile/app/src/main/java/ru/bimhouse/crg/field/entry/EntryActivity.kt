package ru.bimhouse.crg.field.entry

import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.graphics.Color
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.callbackFlow

class EntryActivity : ComponentActivity() {
    private val model: EntryModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val connectivity = getSystemService(ConnectivityManager::class.java)
        val connection = observeConnection(connectivity)
        setContent {
            val online by connection.collectAsState(initial = false)
            MaterialTheme(colorScheme = if (isSystemInDarkTheme()) nightColors else dayColors) {
                FieldEntry(model, online)
            }
        }
    }
}

private fun observeConnection(manager: ConnectivityManager) = callbackFlow {
    fun update() {
        val caps = manager.getNetworkCapabilities(manager.activeNetwork)
        trySend(caps?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) == true)
    }
    val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = update()
        override fun onLost(network: Network) = update()
        override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) = update()
    }
    manager.registerDefaultNetworkCallback(callback)
    update()
    awaitClose { manager.unregisterNetworkCallback(callback) }
}

// Роли из текущей темы BHS.CRG UI. Статусы и действия используют те же роли в обеих темах.
private val dayColors = lightColorScheme(
    primary = Color(0xFF1D5FB0), onPrimary = Color.White,
    primaryContainer = Color(0xFFD6E3FF), onPrimaryContainer = Color(0xFF0B305E),
    secondaryContainer = Color(0xFFD9E3F9), onSecondaryContainer = Color(0xFF192334),
    surface = Color(0xFFF9F9FF), onSurface = Color(0xFF191C20),
    surfaceContainer = Color(0xFFEDEDF4), surfaceContainerLow = Color(0xFFF3F3FA),
    onSurfaceVariant = Color(0xFF44474E), outline = Color(0xFF74777F), outlineVariant = Color(0xFFC4C6CF),
)

private val nightColors = darkColorScheme(
    primary = Color(0xFFA9C7FF), onPrimary = Color(0xFF0B305E),
    primaryContainer = Color(0xFF264777), onPrimaryContainer = Color(0xFFD6E3FF),
    secondaryContainer = Color(0xFF3E4758), onSecondaryContainer = Color(0xFFD9E3F9),
    surface = Color(0xFF111318), onSurface = Color(0xFFE2E2E9),
    surfaceContainer = Color(0xFF1D2024), surfaceContainerLow = Color(0xFF191C20),
    onSurfaceVariant = Color(0xFFC4C6CF), outline = Color(0xFF8E9099), outlineVariant = Color(0xFF44474E),
)
