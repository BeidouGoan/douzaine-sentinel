package com.yq.sentinel.net

import java.net.Inet4Address
import java.net.NetworkInterface

/**
 * 本机网络地址查询。
 *
 * ★ 为什么不用 WifiManager.getConnectionInfo().ipAddress：
 *   1) 它返回的是 int（小端序），要手动按位拼字符串，容易写反；
 *   2) Android 10 起该接口标记废弃，部分 ROM 在关闭定位时返回 0；
 *   3) 我们真正需要的只是「PC 该连哪个地址」，网卡层的信息更直接。
 *
 * ★ 为什么只认 wlan0 / ap0：
 *   手机插着 USB 共享网时还会有 rndis0（192.168.42.x）。
 *   那个地址是给 USB 通道用的，用户在手机上看到它反而会填错，
 *   所以这里只报 WiFi / 热点的地址。
 */
object NetInfo {

    /** 返回当前 WiFi/热点的 IPv4 地址；没有则返回空串 */
    fun wifiIpv4(): String {
        try {
            val ifaces = NetworkInterface.getNetworkInterfaces() ?: return ""
            for (nif in ifaces) {
                if (!nif.isUp || nif.isLoopback) continue
                val name = nif.name ?: continue
                if (!name.startsWith("wlan") && !name.startsWith("ap")) continue
                for (addr in nif.inetAddresses) {
                    if (addr is Inet4Address && !addr.isLoopbackAddress) {
                        val host = addr.hostAddress
                        if (!host.isNullOrEmpty()) return host
                    }
                }
            }
        } catch (_: Exception) {
            // 拿不到就当没有，不打断主流程
        }
        return ""
    }
}
