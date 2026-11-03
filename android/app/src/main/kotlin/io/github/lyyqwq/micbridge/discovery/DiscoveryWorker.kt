package io.github.lyyqwq.micbridge.discovery

import java.net.DatagramSocket

// 每轮发现独占线程和 socket，旧线程退出不能清理新一轮的资源。
internal class DiscoveryWorker {
    @Volatile private var current: Thread? = null

    @Synchronized
    fun start(open: () -> DatagramSocket?, loop: (DatagramSocket, () -> Boolean) -> Unit) {
        if (current != null) return
        val worker = Thread({
            val owner = Thread.currentThread()
            var connection: DatagramSocket? = null
            try {
                connection = open()
                if (connection != null) loop(connection) { current === owner }
            } catch (_: InterruptedException) {
                owner.interrupt()
            } finally {
                connection?.close()
                synchronized(this) {
                    if (current === owner) current = null
                }
            }
        }, "micbridge-udp-discovery")
        current = worker
        worker.start()
    }

    @Synchronized
    fun stop() {
        val previous = current
        current = null
        // receive 使用有限超时；只取消线程，socket 由拥有它的线程在 finally 释放。
        // 避免从 stop 关闭正在阻塞接收的 socket，也避免旧线程碰到新一轮资源。
        previous?.interrupt()
    }
}
