package io.github.arkivx0.lanternly

/**
 * UI color tokens and bulb swatches, mirrored from spec/theme_tokens.json (TokensTest fails on drift).
 * Muted text (DIM) is at least 5:1 on BG, CARD and CTL (WCAG AA for small text).
 */
object Tokens {
    const val BG = 0xFF1A1B26.toInt(); const val CARD = 0xFF24283B.toInt(); const val CTL = 0xFF2F3549.toInt(); const val CTL_HOVER = 0xFF3B4261.toInt()
    const val LINE = 0xFF565F89.toInt(); const val FG = 0xFFC0CAF5.toInt(); const val DIM = 0xFF9AA5CE.toInt(); const val ACCENT = 0xFFF25C54.toInt()
    const val ON_ACCENT = 0xFF1A1B26.toInt(); const val WARN = 0xFFE0AF68.toInt(); const val ON_WARN = 0xFF1A1B26.toInt(); const val OK = 0xFF9ECE6A.toInt(); const val FOCUS = 0xFF7AA2F7.toInt()
    val COLORS = mapOf("bg" to BG, "card" to CARD, "ctl" to CTL, "ctlHover" to CTL_HOVER, "line" to LINE, "fg" to FG, "dim" to DIM,
        "accent" to ACCENT, "onAccent" to ON_ACCENT, "warn" to WARN, "onWarn" to ON_WARN, "ok" to OK, "focus" to FOCUS)
    /** Named bulb colors (kept separate from the UI colors). */
    val SWATCHES = listOf("Red" to 0xF7768E, "Orange" to 0xFF9E64, "Amber" to 0xE0AF68, "Green" to 0x9ECE6A, "Teal" to 0x73DACA, "Blue" to 0x7AA2F7, "Violet" to 0xBB9AF7)
    const val MIN_TOUCH_DP = 48
}
