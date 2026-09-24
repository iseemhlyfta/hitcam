package io.github.hitnes.hitcam.video

import android.graphics.SurfaceTexture
import android.opengl.EGL14
import android.opengl.EGLConfig
import android.opengl.EGLContext
import android.opengl.EGLDisplay
import android.opengl.EGLExt
import android.opengl.EGLSurface
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.opengl.Matrix
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.FloatBuffer
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * How camera frames become the stream. The camera's SurfaceTexture transform already makes the picture upright for
 * the phone held in its natural (portrait) orientation, and mirrors the front camera; on top of that the picture is
 * turned level with the horizon for the current landscape orientation, turned by the user's rotation, and mirrored
 * only if the user asked for it.
 */
data class FrameGeometry(
    /** Display rotation in degrees (Surface.ROTATION_* × 90): the stream follows the phone's landscape orientation. */
    val displayRotation: Int = 90,
    /** Clockwise, as in the protocol's `rotation`. */
    val userRotation: Int = 0,
    val mirror: Boolean = false,
    /** The SurfaceTexture shows the front camera like a mirror; the stream is not mirrored unless [mirror] is set. */
    val frontFacing: Boolean = false,
    /** Sensor orientation of the camera, used to know the buffer's aspect ratio after rotation. */
    val sensorOrientation: Int = 90,
    /** Added to camera timestamps to bring them to System.nanoTime (camera clocks may count time in deep sleep). */
    val clockOffsetNanos: Long = 0,
)

/**
 * Renders camera frames with OpenGL ES 2 into the encoder's input surface and the on-screen preview.
 * Everything GL runs on one thread; surface changes are synchronous so callers can release them right after.
 */
class GlPipeline {
    private val thread = HandlerThread("hitcam-gl").apply { start() }
    private val handler = Handler(thread.looper)

    private var display: EGLDisplay = EGL14.EGL_NO_DISPLAY
    private var context: EGLContext = EGL14.EGL_NO_CONTEXT
    private var config: EGLConfig? = null
    private var idleSurface: EGLSurface = EGL14.EGL_NO_SURFACE

    private var program = 0
    private var texture = 0
    private var positionHandle = 0
    private var texCoordHandle = 0
    private var matrixHandle = 0
    private lateinit var surfaceTexture: SurfaceTexture
    private lateinit var inputSurface: Surface
    private var bufferWidth = 1920
    private var bufferHeight = 1080

    private var encoder: Target? = null
    private var preview: Target? = null
    @Volatile private var geometry = FrameGeometry()

    private val stMatrix = FloatArray(16)
    private val frameMatrix = FloatArray(16)
    // Output (encoded picture) texture coordinates → camera buffer texture coordinates, for focus taps.
    @Volatile private var lastFrameMatrix = FloatArray(16).also { Matrix.setIdentityM(it, 0) }

    private class Target(val surface: EGLSurface, val width: Int, val height: Int)

    init {
        runSync { setUp() }
    }

    /** The surface the camera draws into, sized to the camera output (the GL thread scales and rotates it). */
    fun cameraSurface(width: Int, height: Int): Surface = runSync {
        bufferWidth = width
        bufferHeight = height
        surfaceTexture.setDefaultBufferSize(width, height)
        inputSurface
    }

    fun setGeometry(geometry: FrameGeometry) {
        this.geometry = geometry
    }

    val currentGeometry: FrameGeometry get() = geometry

    fun setEncoderSurface(surface: Surface?, width: Int, height: Int) = runSync {
        encoder = replace(encoder, surface, width, height)
    }

    fun setPreviewSurface(surface: Surface?, width: Int, height: Int) = runSync {
        preview = replace(preview, surface, width, height)
    }

    /**
     * Maps a point of the streamed picture (0…1, y down) to the camera buffer (0…1, y down),
     * so a tap on the preview can pick a focus region.
     */
    fun outputToBuffer(x: Float, y: Float): Pair<Float, Float> {
        val result = FloatArray(4)
        Matrix.multiplyMV(result, 0, lastFrameMatrix, 0, floatArrayOf(x, 1 - y, 0f, 1f), 0)
        return result[0].coerceIn(0f, 1f) to (1 - result[1]).coerceIn(0f, 1f)
    }

    fun release() {
        runSync {
            encoder?.let { EGL14.eglDestroySurface(display, it.surface) }
            preview?.let { EGL14.eglDestroySurface(display, it.surface) }
            encoder = null
            preview = null
            surfaceTexture.release()
            inputSurface.release()
            GLES20.glDeleteProgram(program)
            EGL14.eglMakeCurrent(display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT)
            EGL14.eglDestroySurface(display, idleSurface)
            EGL14.eglDestroyContext(display, context)
            EGL14.eglTerminate(display)
        }
        thread.quitSafely()
    }

    // MARK: GL thread

    private fun replace(old: Target?, surface: Surface?, width: Int, height: Int): Target? {
        if (old != null) {
            EGL14.eglMakeCurrent(display, idleSurface, idleSurface, context)
            EGL14.eglDestroySurface(display, old.surface)
        }
        if (surface == null || !surface.isValid) return null
        val eglSurface = EGL14.eglCreateWindowSurface(display, config, surface, intArrayOf(EGL14.EGL_NONE), 0)
        if (eglSurface == null || eglSurface == EGL14.EGL_NO_SURFACE) return null
        return Target(eglSurface, width, height)
    }

    private fun setUp() {
        display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY)
        val version = IntArray(2)
        check(EGL14.eglInitialize(display, version, 0, version, 1)) { "eglInitialize failed" }
        val attributes = intArrayOf(
            EGL14.EGL_RED_SIZE, 8, EGL14.EGL_GREEN_SIZE, 8, EGL14.EGL_BLUE_SIZE, 8, EGL14.EGL_ALPHA_SIZE, 8,
            EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT,
            EGL14.EGL_SURFACE_TYPE, EGL14.EGL_WINDOW_BIT or EGL14.EGL_PBUFFER_BIT,
            EGL_RECORDABLE_ANDROID, 1,
            EGL14.EGL_NONE,
        )
        val configs = arrayOfNulls<EGLConfig>(1)
        val count = IntArray(1)
        check(EGL14.eglChooseConfig(display, attributes, 0, configs, 0, 1, count, 0) && count[0] > 0) { "no EGL config" }
        config = configs[0]
        context = EGL14.eglCreateContext(display, config, EGL14.EGL_NO_CONTEXT, intArrayOf(EGL14.EGL_CONTEXT_CLIENT_VERSION, 2, EGL14.EGL_NONE), 0)
        check(context != EGL14.EGL_NO_CONTEXT) { "eglCreateContext failed" }
        idleSurface = EGL14.eglCreatePbufferSurface(display, config, intArrayOf(EGL14.EGL_WIDTH, 1, EGL14.EGL_HEIGHT, 1, EGL14.EGL_NONE), 0)
        EGL14.eglMakeCurrent(display, idleSurface, idleSurface, context)

        program = createProgram()
        positionHandle = GLES20.glGetAttribLocation(program, "aPosition")
        texCoordHandle = GLES20.glGetAttribLocation(program, "aTexCoord")
        matrixHandle = GLES20.glGetUniformLocation(program, "uTexMatrix")

        val textures = IntArray(1)
        GLES20.glGenTextures(1, textures, 0)
        texture = textures[0]
        GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, texture)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE)
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE)

        surfaceTexture = SurfaceTexture(texture)
        surfaceTexture.setOnFrameAvailableListener({ drawFrame() }, handler)
        inputSurface = Surface(surfaceTexture)
    }

    private fun drawFrame() {
        if (EGL14.eglGetCurrentContext() == EGL14.EGL_NO_CONTEXT) return
        EGL14.eglMakeCurrent(display, idleSurface, idleSurface, context)
        surfaceTexture.updateTexImage()
        surfaceTexture.getTransformMatrix(stMatrix)
        val geometry = geometry
        val timestamp = surfaceTexture.timestamp + geometry.clockOffsetNanos

        encoder?.let { target ->
            computeFrameMatrix(geometry, target.width, target.height)
            lastFrameMatrix = frameMatrix.copyOf()
            draw(target, 0, 0, target.width, target.height)
            EGLExt.eglPresentationTimeANDROID(display, target.surface, timestamp)
            EGL14.eglSwapBuffers(display, target.surface)
        }
        preview?.let { target ->
            // Letterboxed: the preview shows exactly what is streamed.
            val output = encoder ?: target
            computeFrameMatrix(geometry, output.width, output.height)
            val scale = minOf(target.width.toFloat() / output.width, target.height.toFloat() / output.height)
            val width = (output.width * scale).toInt()
            val height = (output.height * scale).toInt()
            draw(target, (target.width - width) / 2, (target.height - height) / 2, width, height)
            EGL14.eglSwapBuffers(display, target.surface)
        }
    }

    /** Output texture coordinates → SurfaceTexture coordinates: crop to aspect, mirror, rotate, then the camera's own transform. */
    private fun computeFrameMatrix(geometry: FrameGeometry, outputWidth: Int, outputHeight: Int) {
        // Counter-clockwise turn of the upright (natural orientation) picture that makes it level, then the user's clockwise turn.
        val angle = geometry.displayRotation - geometry.userRotation
        // The buffer's aspect after the camera transform and the turn above.
        val quarterTurns = ((geometry.sensorOrientation + geometry.displayRotation + geometry.userRotation) / 90) % 2
        val sourceAspect = if (quarterTurns == 0) bufferWidth.toFloat() / bufferHeight else bufferHeight.toFloat() / bufferWidth
        val outputAspect = outputWidth.toFloat() / outputHeight
        var scaleX = 1f
        var scaleY = 1f
        if (sourceAspect > outputAspect) scaleX = outputAspect / sourceAspect else scaleY = sourceAspect / outputAspect
        val flip = geometry.mirror != geometry.frontFacing

        val pre = FloatArray(16)
        Matrix.setIdentityM(pre, 0)
        Matrix.translateM(pre, 0, 0.5f, 0.5f, 0f)
        Matrix.rotateM(pre, 0, -angle.toFloat(), 0f, 0f, 1f)
        Matrix.scaleM(pre, 0, if (flip) -scaleX else scaleX, scaleY, 1f)
        Matrix.translateM(pre, 0, -0.5f, -0.5f, 0f)
        Matrix.multiplyMM(frameMatrix, 0, stMatrix, 0, pre, 0)
    }

    private fun draw(target: Target, x: Int, y: Int, width: Int, height: Int) {
        EGL14.eglMakeCurrent(display, target.surface, target.surface, context)
        GLES20.glViewport(0, 0, target.width, target.height)
        GLES20.glClearColor(0f, 0f, 0f, 1f)
        GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT)
        GLES20.glViewport(x, y, width, height)
        GLES20.glUseProgram(program)
        GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
        GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, texture)
        GLES20.glUniformMatrix4fv(matrixHandle, 1, false, frameMatrix, 0)
        GLES20.glEnableVertexAttribArray(positionHandle)
        GLES20.glVertexAttribPointer(positionHandle, 2, GLES20.GL_FLOAT, false, 0, positions)
        GLES20.glEnableVertexAttribArray(texCoordHandle)
        GLES20.glVertexAttribPointer(texCoordHandle, 2, GLES20.GL_FLOAT, false, 0, texCoords)
        GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
    }

    private fun createProgram(): Int {
        fun shader(type: Int, source: String): Int {
            val shader = GLES20.glCreateShader(type)
            GLES20.glShaderSource(shader, source)
            GLES20.glCompileShader(shader)
            val status = IntArray(1)
            GLES20.glGetShaderiv(shader, GLES20.GL_COMPILE_STATUS, status, 0)
            check(status[0] != 0) { "shader: " + GLES20.glGetShaderInfoLog(shader) }
            return shader
        }
        val program = GLES20.glCreateProgram()
        GLES20.glAttachShader(program, shader(GLES20.GL_VERTEX_SHADER, VERTEX_SHADER))
        GLES20.glAttachShader(program, shader(GLES20.GL_FRAGMENT_SHADER, FRAGMENT_SHADER))
        GLES20.glLinkProgram(program)
        val status = IntArray(1)
        GLES20.glGetProgramiv(program, GLES20.GL_LINK_STATUS, status, 0)
        check(status[0] != 0) { "program: " + GLES20.glGetProgramInfoLog(program) }
        return program
    }

    private fun <T> runSync(body: () -> T): T {
        if (Thread.currentThread() == thread) return body()
        var result: Result<T>? = null
        val done = CountDownLatch(1)
        handler.post {
            result = runCatching(body)
            done.countDown()
        }
        check(done.await(5, TimeUnit.SECONDS)) { "GL thread is not responding" }
        return result!!.getOrThrow()
    }

    private companion object {
        const val EGL_RECORDABLE_ANDROID = 0x3142

        val positions: FloatBuffer = floatBuffer(-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f)
        val texCoords: FloatBuffer = floatBuffer(0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f)

        fun floatBuffer(vararg values: Float): FloatBuffer =
            ByteBuffer.allocateDirect(values.size * 4).order(ByteOrder.nativeOrder()).asFloatBuffer().apply {
                put(values)
                position(0)
            }

        const val VERTEX_SHADER = """
            attribute vec4 aPosition;
            attribute vec4 aTexCoord;
            uniform mat4 uTexMatrix;
            varying vec2 vTexCoord;
            void main() {
                gl_Position = aPosition;
                vTexCoord = (uTexMatrix * aTexCoord).xy;
            }
        """

        const val FRAGMENT_SHADER = """
            #extension GL_OES_EGL_image_external : require
            precision mediump float;
            uniform samplerExternalOES sTexture;
            varying vec2 vTexCoord;
            void main() {
                gl_FragColor = texture2D(sTexture, vTexCoord);
            }
        """
    }
}
