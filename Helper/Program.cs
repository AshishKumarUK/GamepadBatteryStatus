using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Runtime.InteropServices;
using DualSenseAPI;

static class Program
{
	private static bool s_debug;

	static void Main(string[] args)
    {
        try
        {
			// enable debug logs on stderr
			s_debug = args != null && args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase));
            // 1) Try native DualSense HID first
            if (TryDualSenseHid(out int level, out bool charging, out bool full))
            {
                PrintJson(true, level, charging, full);
                return;
            }

			// 2) Fallback: DS4Windows UDP (Cemuhook / DSU)
            if (TryDs4WindowsUdp(out level, out charging, out full))
            {
                PrintJson(true, level, charging, full);
                return;
            }

            // 3) Nothing worked
            Console.WriteLine(JsonSerializer.Serialize(new { connected = false }));
		}

        catch
        {
            Console.WriteLine(JsonSerializer.Serialize(new { connected = false }));
        }
    }

	private static void DebugLog(string msg)
	{
		if (s_debug)
		{
			try { Console.Error.WriteLine(msg); } catch { }
		}
	}

    private static void PrintJson(bool connected, int level, bool charging, bool full)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            connected,
            level,
            charging,
            full
        }));
    }

    // ---------- Native DualSense HID ----------
    private static bool TryDualSenseHid(out int level, out bool charging, out bool full)
    {
        level = 0; charging = false; full = false;

        try
        {
            var ds = DualSense.EnumerateControllers().FirstOrDefault();
            if (ds == null) return false;

            ds.Acquire();

            int tries = 0;
            while (tries < 15)
            {
                var st = ds.InputState.BatteryStatus;
                level = (int)st.Level;     // float -> int
                charging = st.IsCharging;
                full = st.IsFullyCharged;

                if (level > 0 || charging || full)
                {
                    ds.Release();
                    return true; // meaningful value
                }

                Thread.Sleep(100);
                tries++;
            }

            ds.Release();
            // IMPORTANT: return false when still meaningless (lets UDP fallback run)
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ---------- DS4Windows UDP (Cemuhook / DSU) ----------
    // Proper flow: REGISTER client, then INFO request, then read response.
    private static bool TryDs4WindowsUdp(out int levelPercent, out bool charging, out bool full)
    {
        levelPercent = 0; charging = false; full = false;

        const string host = "127.0.0.1";
        const int port = 26760;
        const ushort proto = 1001;
        const uint MSG_REGISTER = 0x100000;
        const uint MSG_INFO = 0x100001;

        var clientId = (uint)Environment.TickCount;

        // 1) REGISTER packet (no payload in simple form)
        byte[] regPacket = BuildDsuPacket(proto, clientId, MSG_REGISTER, Array.Empty<byte>());

        // 2) INFO request for slots 0..3
        byte[] slots = new byte[] { 0, 1, 2, 3 };
        byte[] infoPayload = new byte[4 + slots.Length];
        BitConverter.GetBytes(4).CopyTo(infoPayload, 0);
        Buffer.BlockCopy(slots, 0, infoPayload, 4, slots.Length);
        byte[] infoPacket = BuildDsuPacket(proto, clientId, MSG_INFO, infoPayload);

        DebugLog($"DSU: sending REGISTER, then INFO to {host}:{port}");

        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 400;
            udp.Connect(host, port);
            udp.Send(regPacket, regPacket.Length);
            // brief pause, then INFO
            Thread.Sleep(50);
            udp.Send(infoPacket, infoPacket.Length);

            var start = Environment.TickCount;
            while (Environment.TickCount - start < 1000)
            {
                if (udp.Available <= 0)
                {
                    Thread.Sleep(50);
                    continue;
                }

                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                var resp = udp.Receive(ref ep);
                if (resp.Length < 32) continue;

                // expect "DSUS" response
                if (resp[0] != (byte)'D' || resp[1] != (byte)'S' || resp[2] != (byte)'U' || resp[3] != (byte)'S')
                    continue;

                // correct protocol?
                if (BitConverter.ToUInt16(resp, 4) != proto) continue;

                // only parse INFO messages for battery
                if (BitConverter.ToUInt32(resp, 16) != MSG_INFO) continue;

                // meta layout: [20]slot [21]state [22]model [23]connType [24..29]mac [30]battery
                byte state = resp[21]; // 2 = connected
                if (state != 2) continue;

                byte b = resp[30];
                MapDsuBattery(b, out levelPercent, out charging, out full);

                if (levelPercent > 0 || charging || full)
                    return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static byte[] BuildDsuPacket(ushort proto, uint clientId, uint messageType, byte[] payload)
    {
        int lengthNoHeader = 4 + (payload?.Length ?? 0);
        byte[] packet = new byte[20 + (payload?.Length ?? 0)];
        Encoding.ASCII.GetBytes("DSUC").CopyTo(packet, 0);
        BitConverter.GetBytes(proto).CopyTo(packet, 4);
        BitConverter.GetBytes((ushort)lengthNoHeader).CopyTo(packet, 6);
        // crc at [8..11], zero before computing
        BitConverter.GetBytes(clientId).CopyTo(packet, 12);
        BitConverter.GetBytes(messageType).CopyTo(packet, 16);
        if (payload != null && payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, packet, 20, payload.Length);
        }

        for (int i = 8; i < 12; i++) packet[i] = 0;
        uint crc = Crc32(packet);
        BitConverter.GetBytes((int)crc).CopyTo(packet, 8);
        return packet;
    }

    private static void MapDsuBattery(byte b, out int percent, out bool charging, out bool full)
    {
        charging = false; full = false; percent = 0;

        switch (b)
        {
            case 0xEE: charging = true; percent = 50; break;   // charging (unknown exact %)
            case 0xEF: full = true; percent = 100; break;      // charged
            case 0x01: percent = 5; break;     // Dying
            case 0x02: percent = 25; break;    // Low
            case 0x03: percent = 50; break;    // Medium
            case 0x04: percent = 75; break;    // High
            case 0x05: percent = 100; break;   // Full
            default: percent = 0; break;
        }
    }

    // Simple CRC32 (poly 0xEDB88320)
    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            uint c = (crc ^ data[i]) & 0xFF;
            for (int j = 0; j < 8; j++)
            {
                c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : (c >> 1);
            }
            crc = (crc >> 8) ^ c;
        }
        return ~crc;
    }
}
