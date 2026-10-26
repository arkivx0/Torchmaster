package io.github.arkivx0.lanternly.core

/**
 * Which bulbs are blinking ("Find this bulb"). Held by the Activity, not the row, so it survives row rebuilds
 * and lets the app send every pending stop before shutdown. Use from the main thread only.
 */
class Blinks<K, V> {
    private val active = LinkedHashMap<K, Pair<Int, V>>()
    private var next = 0

    fun isActive(k: K) = k in active
    val size get() = active.size

    /** Marks k as blinking and returns the id of this blink. */
    fun start(k: K, v: V): Int { val id = ++next; active[k] = id to v; return id }

    /** Manual stop or failed start: true if k was blinking. */
    fun stop(k: K): Boolean = active.remove(k) != null

    /** Ends a failed start only if [id] is still the current blink. */
    fun cancel(k: K, id: Int): Boolean = if (active[k]?.first == id) { active.remove(k); true } else false

    /** Timer end: true (and clears the state) only if blink [id] is still current. */
    fun expire(k: K, id: Int): Boolean = cancel(k, id)

    /** Clears everything and returns what still needs a stop command. */
    fun drain(): List<Pair<K, V>> { val r = active.map { it.key to it.value.second }; active.clear(); return r }
}
