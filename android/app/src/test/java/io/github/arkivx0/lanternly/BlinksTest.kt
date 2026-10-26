package io.github.arkivx0.lanternly

import io.github.arkivx0.lanternly.core.Blinks
import org.junit.Assert.*
import org.junit.Test

class BlinksTest {
    @Test fun stateSurvivesWithoutTheRowAndSecondTapStops() {
        val b = Blinks<String, Int>(); b.start("lamp", 1)
        assertTrue(b.isActive("lamp")) // a rebuilt row reads this, so the next tap is a stop
        assertTrue(b.stop("lamp")); assertFalse(b.isActive("lamp")); assertFalse(b.stop("lamp"))
    }

    @Test fun failedStartResetsOnlyItsOwnBlink() {
        val b = Blinks<String, Int>(); val old = b.start("lamp", 1); b.stop("lamp"); val cur = b.start("lamp", 1)
        assertFalse(b.cancel("lamp", old)); assertTrue(b.isActive("lamp"))
        assertTrue(b.cancel("lamp", cur)); assertFalse(b.isActive("lamp"))
    }

    @Test fun staleTimerDoesNotStopANewerBlink() {
        val b = Blinks<String, Int>(); val old = b.start("lamp", 1); val cur = b.start("lamp", 1)
        assertFalse(b.expire("lamp", old)); assertTrue(b.expire("lamp", cur)); assertEquals(0, b.size)
    }

    @Test fun drainReturnsEveryPendingStop() {
        val b = Blinks<String, Int>(); b.start("a", 1); b.start("b", 2)
        assertEquals(listOf("a" to 1, "b" to 2), b.drain()); assertEquals(0, b.size)
    }
}
