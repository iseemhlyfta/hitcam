package io.github.hitnes.hitcam.ui

import android.annotation.SuppressLint
import android.app.Activity
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.DarkMode
import androidx.compose.material.icons.filled.Flip
import androidx.compose.material.icons.filled.FlashlightOff
import androidx.compose.material.icons.filled.FlashlightOn
import androidx.compose.material.icons.automirrored.filled.RotateRight
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material.icons.filled.Videocam
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilledTonalIconToggleButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.hitnes.hitcam.HitCamApp
import io.github.hitnes.hitcam.R
import io.github.hitnes.hitcam.camera.CameraRules
import io.github.hitnes.hitcam.protocol.CameraState
import io.github.hitnes.hitcam.protocol.Control
import io.github.hitnes.hitcam.protocol.NormalizedPoint
import java.util.Locale

private data class QualityOption(val label: String, val width: Int, val height: Int, val fps: Int, val bitrate: Int)

private val qualityOptions = listOf(
    QualityOption("720p 30", 1280, 720, 30, 4000),
    QualityOption("720p 60", 1280, 720, 60, 6000),
    QualityOption("1080p 30", 1920, 1080, 30, 8000),
    QualityOption("1080p 60", 1920, 1080, 60, 12000),
)

@Composable
fun StreamingScreen(app: HitCamApp, serverName: String) {
    val session = app.session
    val state by session.cameraState.collectAsStateWithLifecycle()
    val fps by session.sentFps.collectAsStateWithLifecycle()
    val kbps by session.sentKbps.collectAsStateWithLifecycle()
    var showControls by rememberSaveable { mutableStateOf(true) }
    var dimmed by remember { mutableStateOf(false) }
    val activity = LocalContext.current as Activity

    fun setDimmed(on: Boolean) {
        dimmed = on
        val attributes = activity.window.attributes
        attributes.screenBrightness = if (on) 0.01f else WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
        activity.window.attributes = attributes
    }
    // The brightness override belongs to this screen only.
    DisposableEffect(Unit) { onDispose { setDimmed(false) } }
    BackHandler(enabled = dimmed) { setDimmed(false) }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        CameraPreview(app, state)

        Column(Modifier.fillMaxSize().systemBarsPadding()) {
            TopBar(serverName, fps, kbps, state, showControls, onToggleControls = { showControls = !showControls }, onDim = { setDimmed(true) }) {
                session.disconnect()
            }
            if (showControls) {
                Row(Modifier.fillMaxSize()) {
                    Spacer(Modifier.weight(1f))
                    Column(
                        Modifier
                            .width(340.dp)
                            .fillMaxHeight()
                            .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.88f))
                            .verticalScroll(rememberScrollState())
                            .padding(12.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Controls(app, state)
                    }
                }
            }
        }

        if (dimmed) {
            Box(
                Modifier.fillMaxSize().background(Color.Black).clickable { setDimmed(false) },
                contentAlignment = Alignment.Center,
            ) {
                Text(stringResource(R.string.tap_to_wake), color = Color.Gray, textAlign = TextAlign.Center, modifier = Modifier.padding(24.dp))
            }
        }
    }
}

@Composable
private fun TopBar(
    serverName: String, fps: Double, kbps: Int, state: CameraState, showControls: Boolean,
    onToggleControls: () -> Unit, onDim: () -> Unit, onDisconnect: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surface.copy(alpha = 0.88f)).padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.Filled.Videocam, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.size(8.dp))
        Column(Modifier.weight(1f)) {
            Text(serverName, fontWeight = FontWeight.Bold, style = MaterialTheme.typography.bodyMedium)
            val size = CameraRules.outputSize(state)
            Text(
                String.format(Locale.US, "%d fps · %.1f Mbit/s · %d×%d", fps.toInt(), kbps / 1000.0, size.width, size.height),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        FilledTonalIconToggleButton(checked = showControls, onCheckedChange = { onToggleControls() }) {
            Icon(Icons.Filled.Tune, contentDescription = stringResource(R.string.controls))
        }
        IconButton(onClick = onDim) { Icon(Icons.Filled.DarkMode, contentDescription = stringResource(R.string.dim)) }
        IconButton(onClick = onDisconnect) {
            Icon(Icons.Filled.Close, contentDescription = stringResource(R.string.disconnect), tint = MaterialTheme.colorScheme.error)
        }
    }
}

@SuppressLint("ClickableViewAccessibility")
@Composable
private fun CameraPreview(app: HitCamApp, state: CameraState) {
    val currentState by rememberUpdatedState(state)
    AndroidView(
        modifier = Modifier.fillMaxSize(),
        factory = { context ->
            SurfaceView(context).apply {
                holder.addCallback(object : SurfaceHolder.Callback {
                    override fun surfaceCreated(holder: SurfaceHolder) = Unit
                    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
                        app.video.setPreviewSurface(holder.surface, width, height)
                    }

                    override fun surfaceDestroyed(holder: SurfaceHolder) {
                        app.video.setPreviewSurface(null, 0, 0)
                    }
                })
                setOnTouchListener { view, event ->
                    if (event.action == MotionEvent.ACTION_UP) {
                        // The preview is letterboxed: map the tap into the streamed picture.
                        val output = CameraRules.outputSize(currentState)
                        val scale = minOf(view.width.toFloat() / output.width, view.height.toFloat() / output.height)
                        val width = output.width * scale
                        val height = output.height * scale
                        val x = (event.x - (view.width - width) / 2) / width
                        val y = (event.y - (view.height - height) / 2) / height
                        if (x in 0f..1f && y in 0f..1f) app.session.apply(Control(focusPoint = NormalizedPoint(x.toDouble(), y.toDouble())))
                    }
                    true
                }
            }
        },
    )
}

@Composable
private fun Controls(app: HitCamApp, state: CameraState) {
    val session = app.session
    val cameras = session.capabilities.cameras
    val camera = cameras.firstOrNull { it.id == state.cameraId }

    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxWidth()) {
        cameras.forEach { info ->
            FilterChip(
                selected = info.id == state.cameraId,
                onClick = { session.apply(Control(cameraId = info.id)) },
                label = { Text(cameraLabel(info.id, info.name)) },
            )
        }
    }

    if (camera != null && camera.maxZoom > camera.minZoom) {
        LabeledSlider(
            label = stringResource(R.string.zoom), value = state.zoom.toFloat(),
            range = camera.minZoom.toFloat()..camera.maxZoom.toFloat(),
            valueText = { String.format(Locale.US, "%.1f×", it) },
        ) { session.apply(Control(zoom = it.toDouble())) }
    }

    LabeledSlider(
        label = stringResource(R.string.exposure), value = state.exposureBias.toFloat(), range = -2f..2f,
        valueText = { String.format(Locale.US, "%+.1f", it) },
    ) { session.apply(Control(exposureBias = it.toDouble())) }

    if (camera?.supportsFocus == true) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.weight(1f)) {
                LabeledSlider(
                    label = stringResource(R.string.focus), value = state.lensPosition.toFloat(), range = 0f..1f,
                    valueText = { String.format(Locale.US, "%.2f", it) },
                ) { session.apply(Control(focusMode = "locked", lensPosition = it.toDouble())) }
            }
            FilterChip(
                selected = state.focusMode == "continuous",
                onClick = { session.apply(Control(focusMode = "continuous")) },
                label = { Text(stringResource(R.string.auto_focus)) },
            )
        }
    }

    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
        var menu by remember { mutableStateOf(false) }
        Box {
            OutlinedButton(onClick = { menu = true }) { Text("${state.height}p${state.fps}") }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                qualityOptions.forEach { option ->
                    DropdownMenuItem(text = { Text(option.label) }, onClick = {
                        menu = false
                        session.apply(Control(width = option.width, height = option.height, fps = option.fps, bitrateKbps = option.bitrate))
                    })
                }
            }
        }
        FilledTonalIconToggleButton(checked = state.mirror, onCheckedChange = { session.apply(Control(mirror = it)) }) {
            Icon(Icons.Filled.Flip, contentDescription = stringResource(R.string.mirror))
        }
        OutlinedButton(onClick = { session.apply(Control(rotation = (state.rotation + 90) % 360)) }) {
            Icon(Icons.AutoMirrored.Filled.RotateRight, contentDescription = stringResource(R.string.rotate))
            Spacer(Modifier.size(4.dp))
            Text("${state.rotation}°")
        }
        if (camera?.hasTorch == true) {
            FilledTonalIconToggleButton(checked = state.torch, onCheckedChange = { session.apply(Control(torch = it)) }) {
                Icon(if (state.torch) Icons.Filled.FlashlightOn else Icons.Filled.FlashlightOff, contentDescription = stringResource(R.string.torch))
            }
        }
    }

    Text(stringResource(R.string.keep_app_open), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun cameraLabel(id: String, name: String): String = when (id) {
    "back-ultrawide" -> "0.5×"
    "back-wide" -> "1×"
    "back-tele" -> stringResource(R.string.camera_tele)
    "front" -> stringResource(R.string.camera_front)
    else -> name
}

/** A slider that follows the phone's state but keeps the finger's value while dragging. */
@Composable
private fun LabeledSlider(
    label: String, value: Float, range: ClosedFloatingPointRange<Float>, valueText: (Float) -> String, onChange: (Float) -> Unit,
) {
    var dragging by remember { mutableStateOf(false) }
    var local by remember { mutableFloatStateOf(value) }
    val shown = if (dragging) local else value.coerceIn(range.start, range.endInclusive)
    Column {
        Row {
            Text(label, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
            Text(valueText(shown), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Slider(
            value = shown,
            valueRange = range,
            onValueChange = {
                dragging = true
                local = it
                onChange(it)
            },
            onValueChangeFinished = { dragging = false },
        )
    }
}
