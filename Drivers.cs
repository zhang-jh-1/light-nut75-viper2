// 设备驱动：NUT75 键鼠（0x0C45:0xFEF9）+ 雷蛇毒蝰 V2 Pro（0x1532:0x00A6 无线 / 0x00A5 有线）
// 协议来源：
//   NUT75    — usagePage 0xFF60，必须同时打开设备的全部 HID 集合才应答；句柄常驻缓存，出错后重开。
//              请求 64B [AA 10 30 00 00 00 01 ...]（写时前置 0x00），响应 [55 10]... 电量@25 充电@26
//   毒蝰 V2  — Razer 通用 90 字节报文（openrazer / razer-battery-report 的 Windows 实现）：
//              feature report（报告 ID 0）+ 90 字节负载，负载布局
//              [0]status [1]tid [2..3]剩余包 [4]协议 [5]数据长度 [6]命令类 [7]命令号 [8..87]参数 [88]XOR 校验
//              电量 0x07/0x80、充电 0x07/0x84，应答参数区第 1 字节（data[9]）；电量原始值 0-255 需换算百分比
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

class Reading {
    public string Id;
    public bool Connected;
    public int Capacity;
    public bool? Charging; // null = 无硬件充电标志，需上层按趋势推断
}

static class Drivers {
    public class Def {
        public string Id, DefaultName, Accent;
        public ushort Vid;
        public ushort[] Pids;      // 多 PID：无线接收器优先，其次有线，最后 4K 轮询率接收器
        public ushort UsagePage;   // 0 = 按用途自动挑（雷蛇），非 0 = 指定命令通道
        public byte Tid = 0x1F;    // Razer transaction id（毒蝰 V2 Pro 为 0x1F）
        public int WaitMs = 60;    // 下发请求到取应答之间留给设备处理的时间
    }
    public static readonly Def[] Defs = {
        new Def { Id = "nut75", Vid = 0x0C45, Pids = new ushort[] { 0xFEF9 }, UsagePage = 0xFF60, DefaultName = "NUT75 键鼠", Accent = "blue" },
        new Def { Id = "viper2pro", Vid = 0x1532, Pids = new ushort[] { 0x00A6, 0x00A5, 0x00B3 }, DefaultName = "雷蛇毒蝰 V2 Pro", Accent = "green", Tid = 0x1F, WaitMs = 60 },
    };

    static readonly object sync = new object();
    static List<KeyValuePair<string, Hid.Device>> nut75; // NUT75 常驻句柄（path → device）

    // 最近一次有效读数：无线鼠标休眠时不再应答，沿用上次电量比直接显示 "--" 更贴近实感
    static readonly Dictionary<string, Reading> lastOk = new Dictionary<string, Reading>();
    static readonly Dictionary<string, DateTime> lastAt = new Dictionary<string, DateTime>();
    const int StaleMinutes = 60;

    public static Reading Read(Def def) {
        var miss = new Reading { Id = def.Id };
        List<Hid.Interface> ifaces;
        try {
            ifaces = new List<Hid.Interface>();
            foreach (var pid in def.Pids) {
                var got = Hid.Enumerate(def.Vid, pid);
                DuoApp.Log("enum " + def.Id + " pid=" + pid.ToString("X4") + " ifaces=" + got.Count);
                ifaces.AddRange(got);
            }
        } catch { return miss; }
        if (ifaces.Count == 0) return miss; // 设备真的不在
        try {
            var r = def.Id == "nut75" ? ReadNut75(ifaces) : ReadRazer(def, ifaces);
            if (r != null) {
                if (def.Id != "nut75") Remember(r);
                return r;
            }
        } catch { }
        return Last(def.Id) ?? miss; // 在但是不应答（无线休眠）→ 沿用上次读数
    }

    public static void DisposeAll() { lock (sync) InvalidateNut75(); }

    static void Remember(Reading r) {
        lock (sync) { lastOk[r.Id] = r; lastAt[r.Id] = DateTime.Now; }
    }

    static Reading Last(string id) {
        lock (sync) {
            Reading r; DateTime t;
            if (!lastOk.TryGetValue(id, out r) || !lastAt.TryGetValue(id, out t)) return null;
            if ((DateTime.Now - t).TotalMinutes > StaleMinutes) return null;
            return new Reading { Id = id, Connected = true, Capacity = r.Capacity, Charging = r.Charging };
        }
    }

    static void InvalidateNut75() {
        if (nut75 == null) return;
        foreach (var e in nut75) e.Value.Dispose();
        nut75 = null;
    }

    static byte[] Request(params byte[] head) {
        var b = new byte[65]; // [0] = report id 0x00（unnumbered report 前置）+ 64 字节负载
        Array.Copy(head, 0, b, 1, head.Length);
        return b;
    }

    // 读缓冲首字节可能是 report id(0) 也可能直接是数据：两种偏移都认
    static int Sig(byte[] b, byte s0, byte s1) {
        if (b[0] == s0 && b[1] == s1) return 0;
        if (b[0] == 0 && b[1] == s0 && b[2] == s1) return 1;
        return -1;
    }

    static Reading ReadNut75(List<Hid.Interface> ifaces) {
        lock (sync) {
            var miss = new Reading { Id = "nut75" };
            if (nut75 == null) {
                nut75 = new List<KeyValuePair<string, Hid.Device>>();
                foreach (var c in ifaces) { // 个别集合（键盘/鼠标）被独占打不开不致命
                    var d = Hid.Open(c.Path);
                    if (d != null) nut75.Add(new KeyValuePair<string, Hid.Device>(c.Path, d));
                }
            }
            var cmd = ifaces.FirstOrDefault(c => c.UsagePage == 0xFF60) ?? ifaces[0];
            var entry = nut75.FirstOrDefault(e => e.Key == cmd.Path);
            if (entry.Value == null) entry = nut75.FirstOrDefault();
            if (entry.Value == null) { InvalidateNut75(); return miss; }

            var result = Hid.Transact(entry.Value, Request(0xAA, 0x10, 0x30, 0, 0, 0, 0x01), b => {
                int o = Sig(b, 0x55, 0x10);
                if (o < 0) return null;
                int cap = b[o + 25];
                if (cap < 1 || cap > 100) return null;
                return new Reading { Id = "nut75", Connected = true, Capacity = cap, Charging = b[o + 26] != 0 };
            }, 4000, 600);
            if (result == null) InvalidateNut75(); // 拔出/句柄失效：下次轮询全新打开
            return result ?? miss;
        }
    }

    // ---- 雷蛇 ----

    // 控制通道在 USB 接口 0；同一接口里有多个 HID 集合，Windows 上应答的是标准鼠标集合（0x0001/0x0002），
    // 打不开或被系统独占时依次退到厂商集合；命中过的集合下次排最前，避免每轮都从错集合试起
    static readonly Dictionary<string, string> razerHit = new Dictionary<string, string>();

    static IEnumerable<Hid.Interface> OrderRazer(Def def, List<Hid.Interface> ifaces) {
        string hit;
        lock (sync) razerHit.TryGetValue(def.Id, out hit);
        return ifaces
            .OrderBy(c => string.Equals(c.Path, hit, StringComparison.OrdinalIgnoreCase) ? -1 : 0)
            .ThenBy(c => c.FeatureLength >= 91 ? 0 : 1)   // 能装下 90 字节报文的集合才是候选
            .ThenBy(c => InterfaceNo(c.Path) == 0 ? 0 : 1)
            .ThenBy(c => (c.UsagePage == 0x0001 && c.Usage == 0x0002) ? 0 : (c.UsagePage >= 0xFF00 ? 1 : 2))
            .Take(8)
            .ToList();
    }

    static int InterfaceNo(string path) {
        int i = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || path.Length < i + 6) return -1; // 没有 mi_XX 段：单接口设备，认
        int no;
        return int.TryParse(path.Substring(i + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out no) ? no : -1;
    }

    // 90 字节请求负载（不含报告 ID）：状态=新命令，参数全 0，末字节前放 XOR 校验
    static byte[] RazerPayload(byte tid, byte cmdClass, byte cmdId, byte dataSize) {
        var r = new byte[90];
        r[0] = 0x00; // status: 新命令
        r[1] = tid;
        r[5] = dataSize;
        r[6] = cmdClass;
        r[7] = cmdId;
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= r[i];
        r[88] = crc;
        return r;
    }

    // 一问一答：SetFeature 下发 → 等设备处理 → GetFeature 取回；忙/无响应重试 5 次（openrazer 同款策略）
    static int RazerValue(Def def, Hid.Device dev, byte cmdClass, byte cmdId, out string trace) {
        trace = "";
        int len = dev.FeatureLength;
        if (len < 91) { trace = "feature=" + len; return -1; } // 没有 90 字节 feature 通道，不是可用的控制集合
        var req = new byte[len];
        req[0] = 0x00; // 报告 ID
        RazerPayload(def.Tid, cmdClass, cmdId, 0x02).CopyTo(req, 1);
        for (int attempt = 0; attempt < 5; attempt++) {
            if (attempt > 0) Thread.Sleep(10);
            if (!dev.SetFeature(req)) { trace += "setfail" + Marshal.GetLastWin32Error() + " "; return -1; }
            Thread.Sleep(def.WaitMs);
            var resp = new byte[len];
            resp[0] = 0x00;
            if (!dev.GetFeature(resp)) { trace += "getfail" + Marshal.GetLastWin32Error() + " "; return -1; }
            var d = new byte[90];
            Array.Copy(resp, 1, d, 0, 90);
            trace += "s" + d[0].ToString("X2") + " ";
            if (d[0] != 0x02) {                          // 0x01 忙 / 0x04 无响应 → 重试；其余状态直接放弃
                if (d[0] == 0x01 || d[0] == 0x04) continue;
                return -1;
            }
            if (d[6] != cmdClass || d[7] != cmdId) { trace += "mismatch "; continue; } // 接收器在设备休眠时可能回旧帧
            byte crc = 0;
            for (int i = 2; i < 88; i++) crc ^= d[i];
            if (crc != d[88]) { trace += "crc "; continue; }
            return d[9];                                  // 参数区第 1 字节
        }
        return -1;
    }

    static string Show(Hid.Interface c) {
        var sb = new System.Text.StringBuilder();
        sb.Append("usage=").Append(c.UsagePage.ToString("X4")).Append('/').Append(c.Usage.ToString("X4"));
        sb.Append(" mi=").Append(InterfaceNo(c.Path));
        sb.Append(" feat=").Append(c.FeatureLength);
        return sb.ToString();
    }

    static Reading ReadRazer(Def def, List<Hid.Interface> ifaces) {
        var order = OrderRazer(def, ifaces).ToList();
        DuoApp.Log("razer candidates=" + order.Count);
        foreach (var c in order) {
            var dev = Hid.Open(c.Path);
            if (dev == null) { DuoApp.Log("razer open fail " + Show(c) + " err=" + Marshal.GetLastWin32Error() + " " + c.Path); continue; }
            try {
                string trace;
                int raw = RazerValue(def, dev, 0x07, 0x80, out trace);
                DuoApp.Log("razer battery raw=" + raw + " [" + trace + "] rw=" + dev.ReadWriteOpen + " " + Show(c));
                if (raw <= 0) continue; // 0 多半是休眠时的占位读数，不可信
                int cap = (raw * 100 + 127) / 255; // 0-255 → 0-100，四舍五入
                int chg = RazerValue(def, dev, 0x07, 0x84, out trace);
                lock (sync) razerHit[def.Id] = c.Path;
                DuoApp.Log("razer ok cap=" + cap + " raw=" + raw + " chg=" + chg + " (" + trace + ") " + Show(c));
                return new Reading {
                    Id = def.Id, Connected = true, Capacity = Math.Min(100, cap),
                    Charging = chg < 0 ? (bool?)null : chg != 0,
                };
            } finally { dev.Dispose(); }
        }
        return null;
    }
}
