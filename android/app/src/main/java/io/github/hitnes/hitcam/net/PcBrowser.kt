package io.github.hitnes.hitcam.net

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Handler
import android.os.Looper
import android.util.Log
import io.github.hitnes.hitcam.protocol.ProtocolInfo
import io.github.hitnes.hitcam.protocol.ServerAddress
import java.util.ArrayDeque

/**
 * A HitCam PC announced on the local network (DNS-SD `_hitcam._tcp`). [claimedId] is only what the announcement says:
 * anyone on the network can announce any id, so it is never trusted for sending a token (see StreamSession); the
 * address alone is used, and a PC not paired at that address asks for its PIN.
 */
data class FoundPc(val name: String, val host: String, val port: Int, val claimedId: String?) {
    /** What to connect to: the address, with the name for display only. */
    val address: ServerAddress get() = ServerAddress(host, port, serverId = null, name = name)

    companion object {
        /** From a resolved announcement; null if it has no usable address. */
        fun from(serviceName: String, host: String?, port: Int, attributes: Map<String, ByteArray?>): FoundPc? {
            if (host.isNullOrEmpty() || port !in 1..65535) return null
            fun text(key: String) = attributes[key]?.toString(Charsets.UTF_8)?.takeIf { it.isNotBlank() }
            return FoundPc(text("name") ?: serviceName, host, port, text("id"))
        }
    }
}

/**
 * Browses for HitCam PCs while started; [onChange] gets the current list on the main thread. NsdManager resolves one
 * service at a time on older Androids, so found services are resolved in turn.
 */
class PcBrowser(context: Context) {
    private val nsd = context.getSystemService(NsdManager::class.java)
    private val main = Handler(Looper.getMainLooper())
    private val found = LinkedHashMap<String, FoundPc>()
    private val toResolve = ArrayDeque<NsdServiceInfo>()
    private var resolving = false
    private var listener: NsdManager.DiscoveryListener? = null

    var onChange: ((List<FoundPc>) -> Unit)? = null

    fun start() {
        if (listener != null || nsd == null) return
        val discovery = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(TAG, "discovery failed: $errorCode")
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit

            override fun onServiceFound(service: NsdServiceInfo) = main.post {
                toResolve.add(service)
                resolveNext()
            }.let { }

            override fun onServiceLost(service: NsdServiceInfo) = main.post {
                if (found.remove(service.serviceName) != null) publish()
            }.let { }
        }
        listener = discovery
        try {
            nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discovery)
        } catch (e: RuntimeException) {
            Log.w(TAG, "discovery not available", e)
            listener = null
        }
    }

    fun stop() {
        val discovery = listener ?: return
        listener = null
        try {
            nsd?.stopServiceDiscovery(discovery)
        } catch (_: RuntimeException) {
        }
        main.post {
            toResolve.clear()
            found.clear()
        }
    }

    @Suppress("DEPRECATION")  // resolveService still works on Android 14+; its replacement needs API 34
    private fun resolveNext() {
        if (resolving || listener == null) return
        val service = toResolve.poll() ?: return
        resolving = true
        try {
            nsd?.resolveService(service, object : NsdManager.ResolveListener {
                override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) = main.post {
                    resolving = false
                    resolveNext()
                }.let { }

                override fun onServiceResolved(serviceInfo: NsdServiceInfo) = main.post {
                    resolving = false
                    FoundPc.from(serviceInfo.serviceName, serviceInfo.host?.hostAddress, serviceInfo.port, serviceInfo.attributes)?.let {
                        found[serviceInfo.serviceName] = it
                        publish()
                    }
                    resolveNext()
                }.let { }
            })
        } catch (e: RuntimeException) {
            resolving = false
            Log.w(TAG, "resolve failed", e)
        }
    }

    private fun publish() {
        if (listener != null) onChange?.invoke(found.values.toList())
    }

    private companion object {
        const val TAG = "HitCamBrowser"
        val SERVICE_TYPE = "${ProtocolInfo.BONJOUR_TYPE}."
    }
}
