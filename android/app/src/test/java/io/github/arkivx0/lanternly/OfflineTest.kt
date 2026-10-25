package io.github.arkivx0.lanternly

import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File

/**
 * Offline guarantee (mirrors the Windows OfflineTests): no GE cloud host name, no network API and no INTERNET
 * permission anywhere in the source of either app.
 */
class OfflineTest {
    private val root: File = generateSequence(File("").absoluteFile) { it.parentFile }.first { File(it, "spec/PROTOCOL.md").exists() }
    private val skip = setOf("build", "bin", "obj", ".gradle", ".kotlin")
    private fun sources() = root.walkTopDown().onEnter { it.name !in skip }.filter { it.isFile && it.extension in setOf("kt", "kts", "xml", "cs", "csproj", "props", "pro") }
        .filter { it.name != "OfflineTest.kt" && it.name != "ProtocolTests.cs" }.toList()
    // Built from pieces so this file never matches its own scan.
    private val hosts = listOf("geli" + "ghting", "xli" + "nk", "cm-" + "sec", "cync" + ".com", "api." + "ge")
    private val apis = listOf("java.net." + "Socket", "javax.net", "Http" + "URLConnection", "Server" + "Socket", "android.permission." + "INTERNET", "Tcp" + "Client", "Http" + "Client", "Ssl" + "Stream")

    @Test fun noGeHostNames() = assertEquals(emptyList<String>(), sources().flatMap { f -> val t = f.readText(); hosts.filter { t.contains(it, ignoreCase = true) }.map { "${f.name}: $it" } })
    @Test fun noNetworkApisOrPermission() = assertEquals(emptyList<String>(), sources().flatMap { f -> val t = f.readText(); apis.filter { t.contains(it) }.map { "${f.name}: $it" } })
}
