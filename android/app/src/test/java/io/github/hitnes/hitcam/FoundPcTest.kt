package io.github.hitnes.hitcam

import io.github.hitnes.hitcam.net.FoundPc
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class FoundPcTest {
    @Test
    fun anAnnouncementGivesTheAddressButNeverATrustedId() {
        val pc = FoundPc.from("DESKTOP-1", "192.168.1.5", 47800, mapOf("id" to "abc".toByteArray(), "name" to "My PC".toByteArray()))!!

        assertEquals("My PC", pc.name)
        assertEquals("abc", pc.claimedId)
        // The id is only claimed: the address to connect to carries none, so no token goes to it before pairing.
        assertNull(pc.address.serverId)
        assertEquals("192.168.1.5", pc.address.host)
        assertEquals(47800, pc.address.port)
    }

    @Test
    fun missingTextFallsBackToTheServiceName() {
        val pc = FoundPc.from("DESKTOP-1", "10.0.0.2", 47801, mapOf("name" to null, "id" to ByteArray(0)))!!
        assertEquals("DESKTOP-1", pc.name)
        assertNull(pc.claimedId)
        assertEquals("10.0.0.2:47801", pc.address.display)
    }

    @Test
    fun anAnnouncementWithoutAnAddressIsDropped() {
        assertNull(FoundPc.from("x", null, 47800, emptyMap()))
        assertNull(FoundPc.from("x", "10.0.0.2", 0, emptyMap()))
    }
}
