package io.github.arkivx0.lanternly

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class TokensTest {
    private val spec = JSONObject(javaClass.classLoader!!.getResource("theme_tokens.json")!!.readText())
    private fun hex(i: Int) = "#%06X".format(i and 0xFFFFFF)
    @Test fun colorsMatchSharedFile() {
        val c = spec.getJSONObject("colors")
        assertEquals(c.keySet(), Tokens.COLORS.keys)
        for (k in c.keySet()) assertEquals(k, c.getString(k).uppercase(), hex(Tokens.COLORS[k]!!))
    }
    @Test fun swatchesMatchSharedFile() {
        val a = spec.getJSONArray("swatches")
        assertEquals(a.length(), Tokens.SWATCHES.size)
        for (i in 0 until a.length()) { assertEquals(a.getJSONArray(i).getString(0), Tokens.SWATCHES[i].first); assertEquals(a.getJSONArray(i).getString(1).uppercase(), hex(Tokens.SWATCHES[i].second)) }
    }
    private fun lum(c: Int): Double { fun ch(v: Int) = (v / 255.0).let { if (it <= 0.03928) it / 12.92 else Math.pow((it + 0.055) / 1.055, 2.4) }
        return 0.2126 * ch(c shr 16 and 255) + 0.7152 * ch(c shr 8 and 255) + 0.0722 * ch(c and 255) }
    private fun contrast(a: Int, b: Int) = (maxOf(lum(a), lum(b)) + 0.05) / (minOf(lum(a), lum(b)) + 0.05)
    @Test fun smallTextContrastAtLeastAA() {
        for (bg in listOf(Tokens.BG, Tokens.CARD, Tokens.CTL)) { assertTrue(contrast(Tokens.DIM, bg) >= 4.5); assertTrue(contrast(Tokens.FG, bg) >= 4.5) }
        assertTrue(contrast(Tokens.ON_WARN, Tokens.WARN) >= 4.5); assertTrue(contrast(Tokens.ON_ACCENT, Tokens.ACCENT) >= 4.5)
    }
}
