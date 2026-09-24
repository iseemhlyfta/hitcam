package io.github.hitnes.hitcam.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import io.github.hitnes.hitcam.HitCamApp
import io.github.hitnes.hitcam.R
import io.github.hitnes.hitcam.protocol.ProtocolInfo
import io.github.hitnes.hitcam.protocol.ServerAddress
import io.github.hitnes.hitcam.session.Phase
import io.github.hitnes.hitcam.session.SessionError

// The PC app's dark palette (violet accent).
private val Accent = Color(0xFFB69CFF)
private val OnAccent = Color(0xFF1B0F33)
private val Ground = Color(0xFF1F1F1E)
private val SurfaceColor = Color(0xFF2A2A28)
private val Surface2 = Color(0xFF323230)
private val TextColor = Color(0xFFF2F1EE)
private val Text2 = Color(0xFFB3B0A8)
val ErrorColor = Color(0xFFFF8A80)

@Composable
fun HitCamTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = Accent, onPrimary = OnAccent, secondary = Accent, onSecondary = OnAccent,
            primaryContainer = Color(0xFF2E2345), onPrimaryContainer = Color(0xFFCDB8FF),
            secondaryContainer = Color(0xFF2E2345), onSecondaryContainer = Color(0xFFCDB8FF),
            background = Ground, onBackground = TextColor, surface = SurfaceColor, onSurface = TextColor,
            surfaceVariant = Surface2, onSurfaceVariant = Text2, surfaceContainer = SurfaceColor,
            surfaceContainerHigh = Surface2, surfaceContainerHighest = Surface2, outline = Color(0xFF3D3C39),
            error = ErrorColor,
        ),
        content = content,
    )
}

@Composable
fun Root(app: HitCamApp, phase: Phase) {
    Surface(color = MaterialTheme.colorScheme.background, modifier = Modifier.fillMaxSize()) {
        when (phase) {
            Phase.Idle -> ConnectScreen(app, null)
            is Phase.Failed -> ConnectScreen(app, phase.error)
            is Phase.Connecting -> ProgressScreen(app, stringResource(R.string.connecting), phase.address.display)
            is Phase.Reconnecting -> ProgressScreen(app, stringResource(R.string.reconnecting), phase.address.display)
            is Phase.Pairing -> PinScreen(app, phase.address.name ?: phase.address.display, phase.attemptsLeft, phase.wrongPin)
            is Phase.Streaming -> StreamingScreen(app, phase.serverName)
        }
    }
}

@Composable
private fun errorText(error: SessionError): String = when (error) {
    is SessionError.ConnectionFailed -> stringResource(R.string.connection_failed, error.reason)
    SessionError.ConnectionClosed -> stringResource(R.string.connection_closed)
    SessionError.ProtocolError -> stringResource(R.string.protocol_error)
    SessionError.OtherPc -> stringResource(R.string.other_pc)
    SessionError.Busy -> stringResource(R.string.busy)
    SessionError.PairingLocked -> stringResource(R.string.pairing_locked)
    SessionError.VersionMismatch -> stringResource(R.string.version_mismatch)
    SessionError.ClosedByPc -> stringResource(R.string.closed_by_pc)
    is SessionError.CameraFailed -> stringResource(R.string.camera_failed, error.reason)
}

@Composable
private fun ConnectScreen(app: HitCamApp, error: SessionError?) {
    val context = LocalContext.current
    val recent = remember { app.localStore.recentServers }
    var addressText by rememberSaveable { mutableStateOf(recent.firstOrNull()?.display ?: "") }
    var inputError by remember { mutableStateOf<String?>(null) }
    var cameraDenied by remember { mutableStateOf(false) }
    var pending by remember { mutableStateOf<ServerAddress?>(null) }
    val invalidAddress = stringResource(R.string.invalid_address)
    val scanPrompt = stringResource(R.string.scan_prompt)

    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        cameraDenied = !granted
        val address = pending
        pending = null
        if (granted && address != null) app.session.connect(address)
    }

    fun start(address: ServerAddress) {
        inputError = null
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            app.session.connect(address)
        } else {
            pending = address
            permission.launch(Manifest.permission.CAMERA)
        }
    }

    fun connectToTypedAddress() {
        val address = ServerAddress.parse(addressText) ?: run {
            inputError = invalidAddress
            return
        }
        // Reuse the id/name remembered from an earlier QR scan of the same PC, so its token is found.
        start(recent.firstOrNull { it.host == address.host && it.port == address.port } ?: address)
    }

    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        val code = result.contents ?: return@rememberLauncherForActivityResult
        val address = ServerAddress.parse(code)
        if (address != null && code.startsWith(ProtocolInfo.URI_SCHEME)) start(address) else inputError = invalidAddress
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .systemBarsPadding()
            .imePadding()
            .verticalScroll(rememberScrollState())
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Column(Modifier.widthIn(max = 480.dp).fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Image(
                    painter = painterResource(R.mipmap.ic_launcher),
                    contentDescription = null,
                    modifier = Modifier.size(56.dp).clip(RoundedCornerShape(14.dp)),
                )
                Spacer(Modifier.size(16.dp))
                Column {
                    Text("HitCam", fontSize = 32.sp, fontWeight = FontWeight.Bold)
                    Text(stringResource(R.string.app_subtitle), color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            Spacer(Modifier.height(28.dp))

            OutlinedTextField(
                value = addressText,
                onValueChange = { addressText = it },
                label = { Text(stringResource(R.string.address_placeholder)) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Go),
                keyboardActions = KeyboardActions(onGo = { connectToTypedAddress() }),
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(12.dp))
            Button(
                onClick = { connectToTypedAddress() },
                enabled = addressText.isNotBlank(),
                modifier = Modifier.fillMaxWidth(),
            ) { Text(stringResource(R.string.connect)) }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(
                onClick = {
                    scanner.launch(
                        ScanOptions()
                            .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                            .setPrompt(scanPrompt)
                            .setBeepEnabled(false)
                            .setOrientationLocked(false),
                    )
                },
                modifier = Modifier.fillMaxWidth(),
            ) {
                Icon(Icons.Filled.QrCodeScanner, contentDescription = null)
                Spacer(Modifier.size(8.dp))
                Text(stringResource(R.string.scan_qr))
            }

            val message = inputError ?: error?.let { errorText(it) } ?: if (cameraDenied) stringResource(R.string.camera_denied) else null
            if (message != null) {
                Spacer(Modifier.height(16.dp))
                Text(message, color = MaterialTheme.colorScheme.error)
                if (cameraDenied) {
                    TextButton(onClick = {
                        context.startActivity(
                            Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", context.packageName, null)),
                        )
                    }) { Text(stringResource(R.string.open_settings)) }
                }
            }

            if (recent.isNotEmpty()) {
                Spacer(Modifier.height(28.dp))
                Text(stringResource(R.string.recent), style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(8.dp))
                Surface(color = MaterialTheme.colorScheme.surface, shape = RoundedCornerShape(12.dp)) {
                    Column {
                        recent.forEachIndexed { index, server ->
                            if (index > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outline)
                            Column(
                                Modifier
                                    .fillMaxWidth()
                                    .clickable { start(server) }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                            ) {
                                Text(server.name ?: server.display)
                                if (server.name != null) {
                                    Text(server.display, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ProgressScreen(app: HitCamApp, title: String, subtitle: String) {
    Column(
        Modifier.fillMaxSize().systemBarsPadding().padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        CircularProgressIndicator()
        Spacer(Modifier.height(16.dp))
        Text(title, style = MaterialTheme.typography.titleMedium)
        Text(subtitle, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(32.dp))
        OutlinedButton(onClick = { app.session.disconnect() }) { Text(stringResource(R.string.cancel)) }
    }
}

@Composable
private fun PinScreen(app: HitCamApp, serverName: String, attemptsLeft: Int, wrongPin: Boolean) {
    var pin by remember { mutableStateOf("") }
    // Set once a PIN is sent; cleared when the PC answers, so one PIN never costs two attempts.
    var awaitingResult by remember { mutableStateOf(false) }
    val focus = remember { FocusRequester() }

    LaunchedEffect(attemptsLeft, wrongPin) {
        pin = ""
        awaitingResult = false
    }
    LaunchedEffect(Unit) { focus.requestFocus() }

    fun submit() {
        if (pin.length != 6 || awaitingResult) return
        awaitingResult = true
        app.session.submitPin(pin)
    }

    Column(
        Modifier.fillMaxSize().systemBarsPadding().imePadding().verticalScroll(rememberScrollState()).padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(Icons.Filled.Lock, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(44.dp))
        Spacer(Modifier.height(12.dp))
        Text(serverName, style = MaterialTheme.typography.titleMedium)
        Text(stringResource(R.string.enter_pin), color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(16.dp))
        OutlinedTextField(
            value = pin,
            onValueChange = { value ->
                pin = value.filter { it.isDigit() }.take(6)
                if (pin.length == 6) submit()
            },
            placeholder = { Text("000000", fontFamily = FontFamily.Monospace, fontSize = 32.sp, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth()) },
            textStyle = MaterialTheme.typography.headlineLarge.copy(fontFamily = FontFamily.Monospace, textAlign = TextAlign.Center, fontWeight = FontWeight.Bold),
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { submit() }),
            modifier = Modifier.widthIn(max = 280.dp).focusRequester(focus),
        )
        if (wrongPin) {
            Spacer(Modifier.height(8.dp))
            Text(stringResource(R.string.wrong_pin, attemptsLeft), color = MaterialTheme.colorScheme.error)
        }
        Spacer(Modifier.height(20.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            OutlinedButton(onClick = { app.session.disconnect() }) { Text(stringResource(R.string.cancel)) }
            Button(onClick = { submit() }, enabled = pin.length == 6 && !awaitingResult) { Text(stringResource(R.string.pair)) }
        }
    }
}
