import io.github.lyyqwq.micbridge.discovery.DiscoveryWorker
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

private fun CountDownLatch.awaitReady() = check(await(5, TimeUnit.SECONDS)) { "worker did not reach checkpoint" }

fun main() {
    val worker = DiscoveryWorker()
    val entered = CountDownLatch(1)
    val release = Semaphore(0)
    val failure = AtomicReference<Throwable?>()
    lateinit var oldThread: Thread
    lateinit var oldSocket: DatagramSocket
    worker.start({
        DatagramSocket(0, InetAddress.getLoopbackAddress()).also { it.soTimeout = 100; oldSocket = it }
    }) { socket, active ->
        oldThread = Thread.currentThread()
        oldThread.uncaughtExceptionHandler = Thread.UncaughtExceptionHandler { _, error -> failure.set(error) }
        check(active())
        entered.countDown()
        // 固定在循环已检查运行状态、即将阻塞接收的位置触发 stop。
        release.acquireUninterruptibly()
        try {
            socket.receive(DatagramPacket(ByteArray(1), 1))
        } catch (_: SocketTimeoutException) {
        }
        check(!active()) { "stopped worker became active after restart" }
    }
    entered.awaitReady()
    worker.stop()

    val restarted = CountDownLatch(1)
    val releaseNew = Semaphore(0)
    lateinit var newThread: Thread
    lateinit var newSocket: DatagramSocket
    worker.start({ DatagramSocket(0, InetAddress.getLoopbackAddress()).also { newSocket = it } }) { _, active ->
        newThread = Thread.currentThread()
        newThread.uncaughtExceptionHandler = Thread.UncaughtExceptionHandler { _, error -> failure.set(error) }
        restarted.countDown()
        releaseNew.acquireUninterruptibly()
        check(active()) { "old cleanup stopped the new worker" }
        check(!newSocket.isClosed) { "old cleanup closed the new socket" }
    }
    restarted.awaitReady()
    release.release()
    oldThread.join(5000)
    // 先释放所有测试线程，再报告失败，避免失败用例挂住 JVM。
    releaseNew.release()
    newThread.join(5000)
    worker.stop()
    check(!oldThread.isAlive && !newThread.isAlive) { "discovery worker did not terminate" }
    check(failure.get() == null) { "stop/restart failed: ${failure.get()}" }
    check(oldSocket.isClosed && newSocket.isClosed) { "worker leaked its socket" }

    val sleeping = CountDownLatch(1)
    lateinit var sendThread: Thread
    lateinit var sendSocket: DatagramSocket
    worker.start({ DatagramSocket().also { sendSocket = it } }) { _, _ ->
        sendThread = Thread.currentThread()
        sendThread.uncaughtExceptionHandler = Thread.UncaughtExceptionHandler { _, error -> failure.set(error) }
        sleeping.countDown()
        Thread.sleep(60_000)
    }
    sleeping.awaitReady()
    worker.stop()
    sendThread.join(5000)
    check(!sendThread.isAlive && sendSocket.isClosed && failure.get() == null)
    println("UDP discovery stop/receive, immediate restart, and sender shutdown checks passed")
}
