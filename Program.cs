using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("Ring Battery Monitor")]
[assembly: AssemblyDescription("Ultra-Lightweight Circular Battery Monitor Widget for Gaming Devices")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("RingBatteryMonitor")]
[assembly: AssemblyProduct("Ring Battery Monitor")]
[assembly: AssemblyCopyright("Copyright (C) 2026")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

namespace RingBatteryMonitor
{
    public class DeviceStatus
    {
        public bool Online;
        public int Level;
        public bool Charging;
        public bool NoLevel;
        public string ShortName;
        public string FullName;
        public double MaxHours;
        public double ChargeMinsPerPct;
    }

    public static class Updater
    {
        // GitHub "owner/repo" that hosts the Releases. Empty = update check disabled.
        public const string Repo = "haruMH/Ring-Battery-Monitor";

        public static Version Current
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static string CurrentText
        {
            get { var v = Current; return v.Major + "." + v.Minor; }
        }

        // Returns true and the release tag / zip URL when a newer release exists. Never throws.
        public static bool TryCheck(out string tag, out string zipUrl)
        {
            tag = null; zipUrl = null;
            if (string.IsNullOrEmpty(Repo)) return false;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
                req.UserAgent = "RingBatteryMonitor";
                req.Timeout = 10000;
                string json;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = sr.ReadToEnd();

                var mt = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                var mz = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");
                if (!mt.Success || !mz.Success) return false;

                Version latest;
                if (!Version.TryParse(mt.Groups[1].Value.TrimStart('v', 'V'), out latest)) return false;
                var cur = Current;
                if (latest.Major * 1000 + latest.Minor <= cur.Major * 1000 + cur.Minor) return false;

                tag = mt.Groups[1].Value;
                zipUrl = mz.Groups[1].Value;
                return true;
            }
            catch (Exception ex)
            {
                NativeEngine.Log("WARN", "Update check failed: " + ex.Message);
                return false;
            }
        }

        // Downloads the release ZIP, then swaps the exe via a helper batch file after this process exits.
        public static bool DownloadAndStage(string zipUrl)
        {
            try
            {
                string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RingBatteryMonitor_update");
                if (Directory.Exists(work)) Directory.Delete(work, true);
                Directory.CreateDirectory(work);
                string zip = System.IO.Path.Combine(work, "update.zip");
                string ext = System.IO.Path.Combine(work, "x");

                System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "RingBatteryMonitor";
                    wc.DownloadFile(zipUrl, zip);
                }
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, ext);

                string[] found = Directory.GetFiles(ext, "RingBatteryMonitor.exe", SearchOption.AllDirectories);
                if (found.Length == 0) return false;

                string self = Assembly.GetExecutingAssembly().Location;
                string bat = System.IO.Path.Combine(work, "apply.bat");
                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("ping 127.0.0.1 -n 4 >nul");
                sb.AppendLine(":retry");
                sb.AppendLine("copy /y \"" + found[0] + "\" \"" + self + "\" >nul 2>&1");
                sb.AppendLine("if errorlevel 1 (ping 127.0.0.1 -n 2 >nul & goto retry)");
                sb.AppendLine("start \"\" \"" + self + "\"");
                File.WriteAllText(bat, sb.ToString(), Encoding.Default);

                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                NativeEngine.Log("WARN", "Update download failed: " + ex.Message);
                return false;
            }
        }
    }

    public class WidgetSettings
    {
        public bool AutoUpdateCheck = true;
        public double Left = 120.0;
        public double Top = 120.0;
        public double Scale = 1.0;
        public bool AutoHideFullScreen = true;
        public bool LowBatteryNotify = true;
        public string BgOpacityMode = "dark";
        public bool LockPosition = false;
        public bool ClickThrough = false;
        public string ColorTheme = "Auto";
        public string BgColor = "#18181B";
        public string BgImagePath = "";

        public static WidgetSettings Load(string path)
        {
            var s = new WidgetSettings();
            if (!File.Exists(path)) return s;
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                s.Left = ParseDouble(json, "Left", s.Left);
                s.Top = ParseDouble(json, "Top", s.Top);
                s.Scale = ParseDouble(json, "Scale", s.Scale);
                s.AutoHideFullScreen = ParseBool(json, "AutoHideFullScreen", s.AutoHideFullScreen);
                s.LowBatteryNotify = ParseBool(json, "LowBatteryNotify", s.LowBatteryNotify);
                s.BgOpacityMode = ParseString(json, "BgOpacityMode", s.BgOpacityMode);
                s.LockPosition = ParseBool(json, "LockPosition", s.LockPosition);
                s.ClickThrough = ParseBool(json, "ClickThrough", s.ClickThrough);
                s.ColorTheme = ParseString(json, "ColorTheme", s.ColorTheme);
                s.BgColor = ParseString(json, "BgColor", s.BgColor);
                s.BgImagePath = ParseString(json, "BgImagePath", s.BgImagePath).Replace("\\\\", "\\");
                s.AutoUpdateCheck = ParseBool(json, "AutoUpdateCheck", s.AutoUpdateCheck);
            }
            catch {}
            return s;
        }

        public void Save(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendFormat(CultureInfo.InvariantCulture, "  \"Left\": {0:F1},\r\n", Left);
                sb.AppendFormat(CultureInfo.InvariantCulture, "  \"Top\": {0:F1},\r\n", Top);
                sb.AppendFormat(CultureInfo.InvariantCulture, "  \"Scale\": {0:F2},\r\n", Scale);
                sb.AppendFormat("  \"AutoHideFullScreen\": {0},\r\n", AutoHideFullScreen ? "true" : "false");
                sb.AppendFormat("  \"LowBatteryNotify\": {0},\r\n", LowBatteryNotify ? "true" : "false");
                sb.AppendFormat("  \"BgOpacityMode\": \"{0}\",\r\n", BgOpacityMode);
                sb.AppendFormat("  \"LockPosition\": {0},\r\n", LockPosition ? "true" : "false");
                sb.AppendFormat("  \"ClickThrough\": {0},\r\n", ClickThrough ? "true" : "false");
                sb.AppendFormat("  \"ColorTheme\": \"{0}\",\r\n", ColorTheme);
                sb.AppendFormat("  \"BgColor\": \"{0}\",\r\n", BgColor);
                sb.AppendFormat("  \"AutoUpdateCheck\": {0},\r\n", AutoUpdateCheck ? "true" : "false");
                sb.AppendFormat("  \"BgImagePath\": \"{0}\"\r\n", (BgImagePath ?? "").Replace("\\", "\\\\"));
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch {}
        }

        private static double ParseDouble(string json, string key, double def)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*([-0-9.]+)");
            if (m.Success)
            {
                double v;
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                    return v;
            }
            return def;
        }

        private static bool ParseBool(string json, string key, bool def)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant() == "true";
            return def;
        }

        private static string ParseString(string json, string key, string def)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
            return def;
        }
    }

    public static class NativeEngine
    {
        public static string LogFilePath;
        private static int _logCount = 0;

        public static void Log(string level, string msg)
        {
            if (string.IsNullOrEmpty(LogFilePath)) return;
            try
            {
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}\r\n", DateTime.Now, level, msg);
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
                _logCount++;
                if (_logCount % 200 == 0)
                {
                    var fi = new FileInfo(LogFilePath);
                    if (fi.Exists && fi.Length > 150 * 1024)
                    {
                        string[] lines = File.ReadAllLines(LogFilePath, Encoding.UTF8);
                        if (lines.Length > 200)
                        {
                            string[] tail = new string[200];
                            Array.Copy(lines, lines.Length - 200, tail, 0, 200);
                            File.WriteAllLines(LogFilePath, tail, Encoding.UTF8);
                        }
                    }
                }
            }
            catch {}
        }

        // HID & SetupAPI
        [DllImport("hid.dll", SetLastError = true)]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDevicePropertyW(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            ref DEVPROPKEY propertyKey,
            out uint propertyType,
            byte[] propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, ref uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject, byte[] lpReportBuffer, int reportBufferLength);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid interfaceClassGuid;
            public int flags;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_ALLCLASSES = 0x04;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;

        private static string _lastMouseShort = "MOUSE";
        private static string _lastMouseFull  = "Wireless Mouse";
        private static string _lastAudioShort = "HEADSET";
        private static string _lastAudioFull  = "Wireless Headset / Audio";

        // ------------------------------------------------------------------
        // ENGINE 1: Windows Native PnP / Bluetooth Classic & BLE GATT Query
        // Resolves ContainerId & BusReportedDeviceDesc so BLE GATT child nodes
        // (Xbox Controller, DualSense, MX Master, Keychron, AirPods, XM5, etc.)
        // always get their true product name and 0-100% battery level.
        // ------------------------------------------------------------------
        public static DeviceStatus GetWindowsPnpBatteryStatus(bool forAudioOrController)
        {
            var status = new DeviceStatus
            {
                Online = false,
                Level = -1,
                Charging = false,
                ShortName = forAudioOrController ? _lastAudioShort : _lastMouseShort,
                FullName = forAudioOrController ? _lastAudioFull : _lastMouseFull,
                MaxHours = forAudioOrController ? 30.0 : 80.0,
                ChargeMinsPerPct = 1.2
            };

            IntPtr devInfo = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) return status;

            try
            {
                SP_DEVINFO_DATA devData = new SP_DEVINFO_DATA();
                devData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                // DEVPKEY_Bluetooth_Battery: {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, pid 2
                DEVPROPKEY keyBtBattery = new DEVPROPKEY { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
                // DEVPKEY_Device_FriendlyName: {A45C254E-DF1C-4EFD-8020-67D146A850E0}, pid 14
                DEVPROPKEY keyFriendly = new DEVPROPKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
                // DEVPKEY_Device_DeviceDesc: {A45C254E-DF1C-4EFD-8020-67D146A850E0}, pid 2
                DEVPROPKEY keyDesc = new DEVPROPKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 2 };
                // DEVPKEY_Device_BusReportedDeviceDesc: {540B947E-8B40-45BC-A8A2-6A0B894CBDA2}, pid 4
                DEVPROPKEY keyBusDesc = new DEVPROPKEY { fmtid = new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), pid = 4 };
                // DEVPKEY_Device_ContainerId: {8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C}, pid 2
                DEVPROPKEY keyContainer = new DEVPROPKEY { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 };

                byte[] buf = new byte[512];
                uint propType, reqSize;

                // Pass 1: Collect friendly names by ContainerId so BLE child battery nodes can look up their parent device name
                var containerNames = new System.Collections.Generic.Dictionary<Guid, string>();
                var batteryNodes = new System.Collections.Generic.List< System.Tuple<Guid, int, string> >();

                uint index = 0;
                while (SetupDiEnumDeviceInfo(devInfo, index++, ref devData))
                {
                    Guid cid = Guid.Empty;
                    if (SetupDiGetDevicePropertyW(devInfo, ref devData, ref keyContainer, out propType, buf, (uint)buf.Length, out reqSize, 0) && reqSize >= 16)
                    {
                        byte[] gbytes = new byte[16];
                        Array.Copy(buf, 0, gbytes, 0, 16);
                        cid = new Guid(gbytes);
                    }

                    string nodeName = "";
                    if (SetupDiGetDevicePropertyW(devInfo, ref devData, ref keyFriendly, out propType, buf, (uint)buf.Length, out reqSize, 0) && reqSize > 2)
                        nodeName = Encoding.Unicode.GetString(buf, 0, (int)reqSize - 2).Trim('\0', ' ');
                    else if (SetupDiGetDevicePropertyW(devInfo, ref devData, ref keyBusDesc, out propType, buf, (uint)buf.Length, out reqSize, 0) && reqSize > 2)
                        nodeName = Encoding.Unicode.GetString(buf, 0, (int)reqSize - 2).Trim('\0', ' ');
                    else if (SetupDiGetDevicePropertyW(devInfo, ref devData, ref keyDesc, out propType, buf, (uint)buf.Length, out reqSize, 0) && reqSize > 2)
                        nodeName = Encoding.Unicode.GetString(buf, 0, (int)reqSize - 2).Trim('\0', ' ');

                    if (!string.IsNullOrEmpty(nodeName) && cid != Guid.Empty)
                    {
                        string lowN = nodeName.ToLowerInvariant();
                        bool isGeneric = lowN.Contains("generic attribute") || lowN.Contains("hid-compliant") ||
                                         lowN.Contains("bluetooth le") || lowN.Contains("avrcp") || lowN.Contains("enumerator");
                        if (!isGeneric && !containerNames.ContainsKey(cid))
                        {
                            containerNames[cid] = nodeName;
                        }
                    }

                    if (SetupDiGetDevicePropertyW(devInfo, ref devData, ref keyBtBattery, out propType, buf, (uint)buf.Length, out reqSize, 0) && reqSize >= 1)
                    {
                        int pct = buf[0];
                        if (pct >= 0 && pct <= 100)
                        {
                            batteryNodes.Add(new System.Tuple<Guid, int, string>(cid, pct, nodeName));
                        }
                    }
                }

                foreach (var item in batteryNodes)
                {
                    Guid cid = item.Item1;
                    int pct = item.Item2;
                    string name = item.Item3;
                    if ((string.IsNullOrEmpty(name) || name.ToLowerInvariant().Contains("generic") || name.ToLowerInvariant().Contains("hid")) &&
                        cid != Guid.Empty && containerNames.ContainsKey(cid))
                    {
                        name = containerNames[cid];
                    }
                    if (string.IsNullOrEmpty(name)) continue;

                    string low = name.ToLowerInvariant();
                    bool isMouseOrInput = low.Contains("mouse") || low.Contains("deathadder") || low.Contains("viper") ||
                                          low.Contains("basilisk") || low.Contains("superlight") || low.Contains("g502") ||
                                          low.Contains("g703") || low.Contains("g304") || low.Contains("g305") || low.Contains("g309") ||
                                          low.Contains("g403") || low.Contains("g603") || low.Contains("g604") || low.Contains("g903") ||
                                          low.Contains("orochi") || low.Contains("pulsar") || low.Contains("lamzu") ||
                                          low.Contains("aerox") || low.Contains("glorious") || low.Contains("mx master") ||
                                          low.Contains("mx anywhere") || low.Contains("mx ergo") || low.Contains("ergo") ||
                                          low.Contains("m575") || low.Contains("m570") || low.Contains("m650") ||
                                          low.Contains("m720") || low.Contains("lift") || low.Contains("pebble") ||
                                          low.Contains("trackball") || low.Contains("keychron") ||
                                          low.Contains("keyboard") || low.Contains("cobra") || low.Contains("naga") ||
                                          low.Contains("g102") || low.Contains("g203") || low.Contains("g402") || low.Contains("g502") ||
                                          low.Contains("g pro x superlight") || low.Contains("g pro wireless") ||
                                          low.Contains("zowie") || low.Contains("finalmouse") || low.Contains("wlmouse") ||
                                          low.Contains("vaxee") || low.Contains("ninjutso") || low.Contains("endgame gear") ||
                                          low.Contains("model o") || low.Contains("model d") || low.Contains("rival") ||
                                          low.Contains("sensei") || low.Contains("prime wireless") || low.Contains("harpoon") ||
                                          low.Contains("dark core") || low.Contains("m65") || low.Contains("scimitar") ||
                                          low.Contains("ironclaw") || low.Contains("sabre") || low.Contains("magic mouse") ||
                                          low.Contains("razer atheris") || low.Contains("hyperx pulsefire") || low.Contains("rog") ||
                                          low.Contains("chakram") || low.Contains("keris") || low.Contains("gladius") ||
                                          low.Contains("k380") || low.Contains("k780") || low.Contains("mx keys") ||
                                          low.Contains("blackwidow") || low.Contains("huntsman") || low.Contains("k70") || low.Contains("k100");

                    bool isAudioOrPad = low.Contains("head") || low.Contains("ear") || low.Contains("buds") ||
                                        low.Contains("airpods") || low.Contains("inzone") || low.Contains("wf-") ||
                                        low.Contains("wh-") || low.Contains("linkbuds") || low.Contains("bose") ||
                                        low.Contains("arctis") || low.Contains("nova") || low.Contains("blackshark") ||
                                        low.Contains("barracuda") || low.Contains("kraken") || low.Contains("g321") ||
                                        low.Contains("g335") || low.Contains("g733") ||
                                        low.Contains("g435") || low.Contains("g535") || low.Contains("astro") ||
                                        low.Contains("zone") || low.Contains("cloud") || low.Contains("hyperx") || low.Contains("jbl") ||
                                        low.Contains("sennheiser") || low.Contains("momentum") || low.Contains("audio") ||
                                        low.Contains("xbox") || low.Contains("controller") || low.Contains("dualsense") ||
                                        low.Contains("dualshock") || low.Contains("gamepad") || low.Contains("joy-con") ||
                                        low.Contains("pro controller") || low.Contains("8bitdo") || low.Contains("stadia") ||
                                        low.Contains("cloud ii") || low.Contains("cloud alpha") || low.Contains("cloud flight") ||
                                        low.Contains("virtuoso") || low.Contains("hs80") || low.Contains("hs70") || low.Contains("void") ||
                                        low.Contains("g pro x") || low.Contains("a50") || low.Contains("a20") || low.Contains("a10") ||
                                        low.Contains("galaxy buds") || low.Contains("pixel buds") || low.Contains("beats") ||
                                        low.Contains("freebuds") || low.Contains("nothing ear") || low.Contains("quietcomfort") ||
                                        low.Contains("jabra") || low.Contains("soundcore") || low.Contains("anker") ||
                                        low.Contains("wh-1000") || low.Contains("wf-1000") || low.Contains("tws") ||
                                        low.Contains("steelseries") || low.Contains("corsair hs") || low.Contains("razer opus") ||
                                        low.Contains("thetis") || low.Contains("kaira") || low.Contains("nari") || low.Contains("joro");

                    if (!forAudioOrController && isMouseOrInput)
                    {
                        status.Online = true;
                        status.Level = pct;
                        status.FullName = name;
                        status.ShortName = ShortenDeviceName(name, "Mouse");
                        return status;
                    }
                    if (forAudioOrController && (isAudioOrPad || !isMouseOrInput))
                    {
                        status.Online = true;
                        status.Level = pct;
                        status.FullName = name;
                        status.ShortName = ShortenDeviceName(name, "Audio");
                        return status;
                    }
                }
            }
            catch {}
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }
            return status;
        }

        public static string ShortenDeviceName(string fullName, string fallback)
        {
            if (string.IsNullOrEmpty(fullName)) return fallback;
            string clean = Regex.Replace(fullName, @"\b(Razer|Logicool|Logitech|SONY|SteelSeries|HyperX|Corsair|Wireless|Gaming|Mouse|Headset|Headphones|Earbuds|Bluetooth|LE|Hands-Free|AG Audio|Stereo)\b", "", RegexOptions.IgnoreCase).Trim();
            clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '_');
            if (clean.Length == 0) clean = fullName.Trim();
            if (clean.Length > 12) clean = clean.Substring(0, 12).Trim();
            return clean;
        }

        // ------------------------------------------------------------------
        // ENGINE 2: Logicool G HUB & Logi Options+ (M575 / MX / LIGHTSPEED)
        // Reads settings.db-wal, settings.db, and settings.json from both
        // LGHUB and LogiOptionsPlus directories.
        // ------------------------------------------------------------------
        public static DeviceStatus GetLogitechGHubStatus(bool forAudio)
        {
            var status = new DeviceStatus
            {
                Online = false,
                Level = -1,
                Charging = false,
                ShortName = forAudio ? "G Headset" : "Logicool",
                FullName = forAudio ? "Logicool Wireless Headset" : "Logicool Wireless Mouse",
                MaxHours = forAudio ? 35.0 : 90.0,
                ChargeMinsPerPct = 1.0
            };

            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                var candidateFiles = new System.Collections.Generic.List<string>();
                string[] dirs = new string[]
                {
                    Path.Combine(localApp, "LGHUB"),
                    Path.Combine(localApp, "LogiOptionsPlus"),
                    Path.Combine(progData, "Logishrd", "LogiOptionsPlus"),
                    Path.Combine(localApp, "Logitech", "LogiOptions")
                };

                foreach (string d in dirs)
                {
                    if (Directory.Exists(d))
                    {
                        candidateFiles.Add(Path.Combine(d, "settings.db-wal"));
                        candidateFiles.Add(Path.Combine(d, "settings.db"));
                        candidateFiles.Add(Path.Combine(d, "settings.json"));
                    }
                }

                foreach (string filePath in candidateFiles)
                {
                    if (!File.Exists(filePath)) continue;
                    byte[] rawBytes;
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length == 0) continue;
                        int maxRead = (int)Math.Min(fs.Length, 10 * 1024 * 1024);
                        rawBytes = new byte[maxRead];
                        fs.Read(rawBytes, 0, maxRead);
                    }

                    string content = Encoding.UTF8.GetString(rawBytes);

                    // Pattern A: G HUB & Options+ "battery/<deviceId>/percentage" : { ... }
                    var matches = Regex.Matches(content, @"battery/([a-zA-Z0-9_\-]+)/percentage""?\s*:\s*\{([^\}]+)\}");
                    for (int i = matches.Count - 1; i >= 0; i--)
                    {
                        string devId = matches[i].Groups[1].Value.ToLowerInvariant();
                        string jsonBody = matches[i].Groups[2].Value;

                        var mPct = Regex.Match(jsonBody, @"""?(?:percentage|batteryLevel|level)""?\s*:\s*(\d{1,3})");
                        if (!mPct.Success) continue;

                        int pct;
                        if (!int.TryParse(mPct.Groups[1].Value, out pct) || pct < 0 || pct > 100) continue;

                        bool isHeadsetDev = devId.Contains("g321") || devId.Contains("g335") || devId.Contains("g733") ||
                                            devId.Contains("g535") || devId.Contains("g435") || devId.Contains("g933") ||
                                            devId.Contains("g935") || devId.Contains("astro") || devId.Contains("a50") ||
                                            devId.Contains("a30") || devId.Contains("headset") || devId.Contains("zone") ||
                                            devId.Contains("prox2") || devId.Contains("pro_x_2") || devId.Contains("pro_x_wireless");

                        if (forAudio == isHeadsetDev)
                        {
                            status.Online = true;
                            status.Level = pct;
                            AssignLogicoolModelName(devId + " " + content, forAudio, ref status);

                            if (jsonBody.IndexOf("\"isCharging\":true", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                jsonBody.IndexOf("CHARGING", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                status.Charging = true;
                            }
                            return status;
                        }
                    }

                    // Pattern B: Logi Options+ device battery entry ("batteryLevel": 90 or "battery_percentage": 90)
                    if (!forAudio && (filePath.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        var mOpt = Regex.Matches(content, @"""?(?:batteryLevel|battery_percentage|soc|percentage)""?\s*:\s*(\d{1,3})");
                        for (int i = mOpt.Count - 1; i >= 0; i--)
                        {
                            int pct;
                            if (int.TryParse(mOpt[i].Groups[1].Value, out pct) && pct > 0 && pct <= 100)
                            {
                                status.Online = true;
                                status.Level = pct;
                                AssignLogicoolModelName(content, false, ref status);
                                return status;
                            }
                        }
                    }
                }
            }
            catch {}
            return status;
        }

        private static void AssignLogicoolModelName(string text, bool forAudio, ref DeviceStatus status)
        {
            string low = text.ToLowerInvariant();
            if (!forAudio)
            {
                if (low.Contains("m575") || low.Contains("ergo m575")) { status.ShortName = "ERGO M575"; status.FullName = "Logicool ERGO M575"; status.MaxHours = 240.0; }
                else if (low.Contains("mx ergo")) { status.ShortName = "MX ERGO"; status.FullName = "Logicool MX ERGO"; status.MaxHours = 120.0; }
                else if (low.Contains("mx master")) { status.ShortName = "MX MASTER"; status.FullName = "Logicool MX Master Wireless"; status.MaxHours = 150.0; }
                else if (low.Contains("mx anywhere")) { status.ShortName = "MX ANYWHERE"; status.FullName = "Logicool MX Anywhere Wireless"; status.MaxHours = 120.0; }
                else if (low.Contains("lift")) { status.ShortName = "LIFT"; status.FullName = "Logicool Lift Vertical Mouse"; status.MaxHours = 240.0; }
                else if (low.Contains("m650")) { status.ShortName = "M650"; status.FullName = "Logicool Signature M650"; status.MaxHours = 240.0; }
                else if (low.Contains("m720")) { status.ShortName = "M720"; status.FullName = "Logicool M720 Triathlon"; status.MaxHours = 240.0; }
                else if (low.Contains("superlight2") || low.Contains("superlight_2") || low.Contains("pro_x_superlight_2")) { status.ShortName = "SUPERLIGHT 2"; status.FullName = "Logicool PRO X SUPERLIGHT 2"; }
                else if (low.Contains("superlight")) { status.ShortName = "SUPERLIGHT"; status.FullName = "Logicool G PRO X SUPERLIGHT"; }
                else if (low.Contains("g502")) { status.ShortName = "G502 X"; status.FullName = "Logicool G502 LIGHTSPEED"; }
                else if (low.Contains("g703")) { status.ShortName = "G703"; status.FullName = "Logicool G703 LIGHTSPEED"; }
                else if (low.Contains("g304") || low.Contains("g305")) { status.ShortName = "G304"; status.FullName = "Logicool G304 LIGHTSPEED"; }
                else if (low.Contains("g309")) { status.ShortName = "G309"; status.FullName = "Logicool G309 LIGHTSPEED"; }
                else if (low.Contains("g903")) { status.ShortName = "G903"; status.FullName = "Logicool G903 LIGHTSPEED"; }
                else { status.ShortName = "Logicool"; status.FullName = "Logicool Wireless Mouse"; }
            }
            else
            {
                if (low.Contains("g321")) { status.ShortName = "G321 WL"; status.FullName = "Logicool G321 LIGHTSPEED"; status.MaxHours = 20.0; }
                else if (low.Contains("g733")) { status.ShortName = "G733"; status.FullName = "Logicool G733 LIGHTSPEED"; }
                else if (low.Contains("g535")) { status.ShortName = "G535"; status.FullName = "Logicool G535 Wireless"; }
                else if (low.Contains("g435")) { status.ShortName = "G435"; status.FullName = "Logicool G435 LIGHTSPEED"; }
                else if (low.Contains("a50")) { status.ShortName = "ASTRO A50"; status.FullName = "Logicool ASTRO A50 Wireless"; }
                else if (low.Contains("zone")) { status.ShortName = "ZONE WL"; status.FullName = "Logicool Zone Wireless"; }
                else { status.ShortName = "PRO X WL"; status.FullName = "Logicool PRO X Wireless Headset"; }
            }
        }

        // ------------------------------------------------------------------
        // ENGINE 2-B: Logitech Direct USB Receiver HID++ 2.0 Query (VID_046D)
        // Directly queries Unifying (C52B), Logi Bolt (C548), and LIGHTSPEED
        // USB receivers for Feature 0x1004 (UnifiedBattery) and 0x1000 (BatteryStatus)
        // so ERGO M575 / MX Master / LIGHTSPEED work over USB even without software!
        // ------------------------------------------------------------------
        public static DeviceStatus GetLogitechHidPpUsbStatus(bool forAudio)
        {
            var status = new DeviceStatus
            {
                Online = false,
                Level = -1,
                Charging = false,
                ShortName = forAudio ? "G Headset" : "ERGO / Logi",
                FullName = forAudio ? "Logicool USB Wireless Headset" : "Logicool USB Wireless Mouse",
                MaxHours = forAudio ? 28.0 : 120.0,
                ChargeMinsPerPct = 1.2
            };

            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) return status;

            var candidatePaths = new System.Collections.Generic.List<string>();
            try
            {
                SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);
                uint index = 0;
                while (SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, index++, ref ifData))
                {
                    uint reqSize = 0;
                    SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, ref reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;
                    IntPtr detailData = Marshal.AllocHGlobal((int)reqSize);
                    Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detailData, reqSize, ref reqSize, IntPtr.Zero))
                    {
                        IntPtr pPath = (IntPtr)((long)detailData + 4);
                        string path = Marshal.PtrToStringAuto(pPath);
                        if (!string.IsNullOrEmpty(path))
                        {
                            string low = path.ToLowerInvariant();
                            // Match Logitech VID_046D vendor HID++ collections
                            if (low.Contains("vid_046d"))
                            {
                                candidatePaths.Add(path);
                            }
                        }
                    }
                    Marshal.FreeHGlobal(detailData);
                }
            }
            catch {}
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }

            if (candidatePaths.Count == 0)
            {
                _cachedHidPpMouse = null;
                return status;
            }

            // If we already have a valid reading from the USB receiver within the last 30 seconds,
            // return the cached reading so we don't spam the wireless RF link every 2 seconds while moving the mouse!
            if (!forAudio && _cachedHidPpMouse != null && (DateTime.UtcNow - _lastHidPpMouseQuery).TotalSeconds < 30.0)
            {
                return _cachedHidPpMouse;
            }

            foreach (string path in candidatePaths)
            {
                string lowPath = path.ToLowerInvariant();
                // Skip standard keyboard/mouse boot collections (col01) if multiple collections exist
                if (lowPath.Contains("mi_00") && lowPath.Contains("col01")) continue;

                DeviceStatus found = QuerySingleHidPpPath(path, forAudio);
                if (found.Online && found.Level >= 0)
                {
                    if (!forAudio)
                    {
                        _cachedHidPpMouse = found;
                        _lastHidPpMouseQuery = DateTime.UtcNow;
                    }
                    return found;
                }
            }

            // If the USB receiver is still plugged in, but the wireless mouse is currently in idle micro-sleep
            // or busy sending movement packets, keep displaying the last known battery level instead of flickering to OFF!
            if (!forAudio && _cachedHidPpMouse != null)
            {
                _lastHidPpMouseQuery = DateTime.UtcNow.AddSeconds(-22.0); // retry again in 8 seconds
                return _cachedHidPpMouse;
            }

            return status;
        }

        private static DeviceStatus _cachedHidPpMouse = null;
        private static DateTime _lastHidPpMouseQuery = DateTime.MinValue;

        private static bool WriteHidPpPacket(SafeFileHandle h, byte devIdx, byte featIdx, byte funcSwId, byte p0, byte p1)
        {
            uint written = 0;
            byte[] reqLong = new byte[20];
            reqLong[0] = 0x11;
            reqLong[1] = devIdx;
            reqLong[2] = featIdx;
            reqLong[3] = funcSwId;
            reqLong[4] = p0;
            reqLong[5] = p1;
            if (WriteFile(h, reqLong, 20, out written, IntPtr.Zero)) return true;

            byte[] reqShort = new byte[7] { 0x10, devIdx, featIdx, funcSwId, p0, p1, 0x00 };
            return WriteFile(h, reqShort, 7, out written, IntPtr.Zero);
        }

        private static bool ReadHidPpResponse(SafeFileHandle h, byte expectedFeatIdx, byte expectedSwId, out byte[] matchedResp)
        {
            matchedResp = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                byte[] resp = new byte[21];
                uint read = 0;
                if (!ReadFile(h, resp, 20, out read, IntPtr.Zero) || read < 7)
                    return false;

                if (resp[0] == 0x10 || resp[0] == 0x11)
                {
                    // Skip HID++ error packet (0x8F) or unrelated notification packets (SW_ID == 0)
                    if (resp[2] == 0x8F || resp[2] == 0xFF) return false;
                    if (resp[2] == expectedFeatIdx && (resp[3] & 0x0F) == expectedSwId)
                    {
                        matchedResp = resp;
                        return true;
                    }
                }
            }
            return false;
        }

        private static DeviceStatus QuerySingleHidPpPath(string path, bool forAudio)
        {
            var result = new DeviceStatus
            {
                Online = false,
                Level = -1,
                Charging = false,
                ShortName = forAudio ? "Logicool WL" : "ERGO M575",
                FullName = forAudio ? "Logicool USB Wireless Audio" : "Logicool ERGO M575 Wireless",
                MaxHours = forAudio ? 25.0 : 240.0,
                ChargeMinsPerPct = 1.2
            };

            var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return result;

            try
            {
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    byte[] devIndices = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xFF };
                    ushort[] battFeatures = new ushort[] { 0x1004, 0x1000 };

                    foreach (byte devIdx in devIndices)
                    {
                        foreach (ushort featId in battFeatures)
                        {
                            byte hi = (byte)((featId >> 8) & 0xFF);
                            byte lo = (byte)(featId & 0xFF);
                            if (!WriteHidPpPacket(h, devIdx, 0x00, 0x0A, hi, lo)) continue;

                            byte[] resp;
                            if (ReadHidPpResponse(h, 0x00, 0x0A, out resp) && resp[4] != 0x00)
                            {
                                byte featIdx = resp[4];
                                byte fnByte = (featId == 0x1004) ? (byte)0x1A : (byte)0x0A;
                                if (!WriteHidPpPacket(h, devIdx, featIdx, fnByte, 0x00, 0x00)) continue;

                                byte[] bResp;
                                if (ReadHidPpResponse(h, featIdx, 0x0A, out bResp))
                                {
                                    int pct = bResp[4];
                                    if (pct > 0 && pct <= 100)
                                    {
                                        result.Online = true;
                                        result.Level = pct;
                                        if (featId == 0x1004)
                                            result.Charging = (bResp[6] >= 1 && bResp[6] <= 3);
                                        else
                                            result.Charging = (bResp[6] >= 1 && bResp[6] <= 4);

                                        // Query Feature 0x0005 (DeviceName) if available
                                        if (WriteHidPpPacket(h, devIdx, 0x00, 0x0C, 0x00, 0x05))
                                        {
                                            byte[] nResp;
                                            if (ReadHidPpResponse(h, 0x00, 0x0C, out nResp) && nResp[4] != 0)
                                            {
                                                byte nameFeat = nResp[4];
                                                if (WriteHidPpPacket(h, devIdx, nameFeat, 0x1C, 0x00, 0x00))
                                                {
                                                    byte[] gnResp;
                                                    if (ReadHidPpResponse(h, nameFeat, 0x0C, out gnResp))
                                                    {
                                                        string hwName = Encoding.ASCII.GetString(gnResp, 4, 14).Trim('\0', ' ');
                                                        if (!string.IsNullOrEmpty(hwName))
                                                        {
                                                            result.FullName = "Logicool " + hwName;
                                                            result.ShortName = ShortenDeviceName(hwName, forAudio ? "Audio" : "ERGO M575");
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        return;
                                    }
                                }
                            }
                        }
                    }
                });

                // Give wireless receiver up to 180ms to reply
                if (!task.Wait(180))
                {
                    h.Close();
                }
            }
            catch {}
            finally
            {
                if (!h.IsClosed) h.Close();
            }
            return result;
        }

        // ------------------------------------------------------------------
        // ENGINE 2-C: USB Audio Dongle Presence Fallback (for G321 LIGHTSPEED / G435)
        // Since G321 LIGHTSPEED's USB transceiver is a plug-and-play USB Audio Class
        // device that does not send % telemetry to PC over USB, detect its active USB
        // presence so the headset slot shows "G321 WL" as connected (100% / ON).
        // ------------------------------------------------------------------
        public static DeviceStatus GetUsbAudioDongleFallback()
        {
            var status = new DeviceStatus
            {
                Online = false,
                Level = -1,
                Charging = false,
                ShortName = "G321 WL",
                FullName = "Logicool G321 LIGHTSPEED (USB)",
                MaxHours = 20.0,
                ChargeMinsPerPct = 1.5
            };

            IntPtr devInfo = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) return status;

            try
            {
                SP_DEVINFO_DATA devData = new SP_DEVINFO_DATA();
                devData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                DEVPROPKEY keyFriendly = new DEVPROPKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
                DEVPROPKEY keyBusDesc  = new DEVPROPKEY { fmtid = new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), pid = 4 };

                byte[] buf = new byte[512];
                uint propType, reqSize;
                uint index = 0;

                while (SetupDiEnumDeviceInfo(devInfo, index++, ref devData))
                {
                    string name = "";
                    if (SetupDiGetDevicePropertyW(devInfo, ref devData, ref keyFriendly, out propType, buf, (uint)buf.Length, out reqSize, 0) && reqSize > 2)
                        name = Encoding.Unicode.GetString(buf, 0, (int)reqSize - 2).Trim('\0', ' ');
                    else if (SetupDiGetDevicePropertyW(devInfo, ref devData, ref keyBusDesc, out propType, buf, (uint)buf.Length, out reqSize, 0) && reqSize > 2)
                        name = Encoding.Unicode.GetString(buf, 0, (int)reqSize - 2).Trim('\0', ' ');

                    if (string.IsNullOrEmpty(name)) continue;
                    string low = name.ToLowerInvariant();
                    if (low.Contains("g321"))
                    {
                        status.Online = true;
                        status.NoLevel = true;
                        status.ShortName = "G321 WL";
                        status.FullName = "Logicool G321 LIGHTSPEED (USB)";
                        return status;
                    }
                    else if (low.Contains("g435"))
                    {
                        status.Online = true;
                        status.NoLevel = true;
                        status.ShortName = "G435 WL";
                        status.FullName = "Logicool G435 LIGHTSPEED (USB)";
                        return status;
                    }
                }
            }
            catch {}
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }
            return status;
        }

        // ------------------------------------------------------------------
        // Diagnostics: log every connected HID VID/PID once at startup so buyers can report
        // devices that are not supported yet (debug.log).
        // ------------------------------------------------------------------
        public static void LogHidInventory()
        {
            try
            {
                Guid hidGuid;
                HidD_GetHidGuid(out hidGuid);
                IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) return;
                var seen = new System.Collections.Generic.HashSet<string>();
                SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);
                uint index = 0;
                while (SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, index++, ref ifData))
                {
                    uint reqSize = 0;
                    SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, ref reqSize, IntPtr.Zero);
                    IntPtr detailData = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detailData, reqSize, ref reqSize, IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringAuto((IntPtr)((long)detailData + 4)) ?? "";
                            var m = Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
                            if (m.Success) seen.Add(m.Groups[1].Value.ToUpperInvariant() + ":" + m.Groups[2].Value.ToUpperInvariant());
                        }
                    }
                    finally { Marshal.FreeHGlobal(detailData); }
                }
                SetupDiDestroyDeviceInfoList(devInfo);
                Log("INFO", "HID devices (VID:PID): " + string.Join(", ", new System.Collections.Generic.List<string>(seen).ToArray()));
            }
            catch {}
        }

        // ------------------------------------------------------------------
        // ENGINE 3: SONY INZONE (H5 / H7 / H9 / Buds) Direct USB HID Query
        // ------------------------------------------------------------------
        public static DeviceStatus GetSonyInzoneStatus()
        {
            var status = new DeviceStatus
            {
                Online = false,
                Level = -1,
                Charging = false,
                ShortName = "INZONE",
                FullName = "SONY INZONE Wireless",
                MaxHours = 28.0,
                ChargeMinsPerPct = 1.8
            };

            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
            ifData.cbSize = Marshal.SizeOf(ifData);

            uint index = 0;
            while (SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, index++, ref ifData))
            {
                uint reqSize = 0;
                SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, ref reqSize, IntPtr.Zero);
                IntPtr detailData = Marshal.AllocHGlobal((int)reqSize);
                Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);

                if (SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detailData, reqSize, ref reqSize, IntPtr.Zero))
                {
                    IntPtr pPath = (IntPtr)((long)detailData + 4);
                    string path = Marshal.PtrToStringAuto(pPath);
                    string lower = path.ToLowerInvariant();

                    // Match SONY VID_054C INZONE control interface (mi_05&col03)
                    if (lower.Contains("vid_054c") && lower.Contains("mi_05&col03"))
                    {
                        if (lower.Contains("pid_0ebf")) { status.ShortName = "INZONE H5"; status.FullName = "SONY INZONE H5"; status.MaxHours = 28.0; }
                        else if (lower.Contains("pid_0df0")) { status.ShortName = "INZONE H9"; status.FullName = "SONY INZONE H9"; status.MaxHours = 32.0; }
                        else if (lower.Contains("pid_0df1")) { status.ShortName = "INZONE H7"; status.FullName = "SONY INZONE H7"; status.MaxHours = 40.0; }
                        else if (lower.Contains("pid_0e70")) { status.ShortName = "INZONE Buds"; status.FullName = "SONY INZONE Buds"; status.MaxHours = 12.0; }

                        Marshal.FreeHGlobal(detailData);
                        SetupDiDestroyDeviceInfoList(devInfo);

                        using (var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                        {
                            if (h.IsInvalid) return status;

                            ushort tid = 0x0042;
                            byte[] cmd = new byte[64];
                            cmd[0] = 0x02;
                            cmd[1] = 12;
                            cmd[2] = 0x01;
                            cmd[3] = 0x00;
                            cmd[4] = 0xFC;
                            cmd[5] = 0x08;
                            cmd[6] = 0x96;
                            cmd[7] = 0xC3;
                            cmd[8] = 0x41;
                            cmd[9] = 0x04;
                            cmd[10] = 0x01;
                            cmd[11] = (byte)(tid & 0xFF);
                            cmd[12] = (byte)((tid >> 8) & 0xFF);

                            uint sum = 0;
                            for (int i = 6; i <= 12; i++) sum += cmd[i];
                            cmd[13] = (byte)(sum & 0xFF);

                            uint written = 0;
                            WriteFile(h, cmd, 64, out written, IntPtr.Zero);

                            for (int attempt = 0; attempt < 8; attempt++)
                            {
                                byte[] resp = new byte[64];
                                uint read = 0;
                                if (ReadFile(h, resp, 64, out read, IntPtr.Zero) || HidD_GetInputReport(h, resp, 64))
                                {
                                    if (resp[0] == 0x02 && resp[9] == 0x04)
                                    {
                                        status.Charging = (resp[13] != 0);
                                        int pct = resp[14];
                                        if (pct != 0xFF && pct >= 0 && pct <= 100)
                                        {
                                            status.Online = true;
                                            status.Level = pct;
                                        }
                                        return status;
                                    }
                                }
                                Thread.Sleep(25);
                            }
                        }
                        return status;
                    }
                }
                Marshal.FreeHGlobal(detailData);
            }
            SetupDiDestroyDeviceInfoList(devInfo);
            return status;
        }

        // ------------------------------------------------------------------
        // ENGINE 4: Razer Universal Wireless Query (Synapse 4 AND Synapse 3)
        // Supports Razer Mice (DeathAdder, Viper, Basilisk, Naga, Cobra, Orochi)
        // AND Razer Headsets (BlackShark V2 Pro, Barracuda, Kraken, Hammerhead)
        // ------------------------------------------------------------------
        public static DeviceStatus GetRazerStatus(bool forAudio)
        {
            var status = new DeviceStatus
            {
                Online = false,
                Level = -1,
                Charging = false,
                ShortName = forAudio ? "Razer Audio" : "Razer Mouse",
                FullName = forAudio ? "Razer Wireless Headset" : "Razer Wireless Mouse",
                MaxHours = forAudio ? 70.0 : 100.0,
                ChargeMinsPerPct = 0.9
            };

            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                // 1. Check Razer Synapse 4 Logs
                string logDir4 = Path.Combine(localApp, @"Razer\RazerAppEngine\User Data\Logs");
                if (Directory.Exists(logDir4))
                {
                    var dirInfo = new DirectoryInfo(logDir4);
                    FileInfo[] files = dirInfo.GetFiles("background-manager*.log");
                    FileInfo latest = null;
                    foreach (var f in files)
                    {
                        if (latest == null || f.LastWriteTimeUtc > latest.LastWriteTimeUtc)
                            latest = f;
                    }

                    if (latest != null)
                    {
                        string content;
                        using (var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            content = sr.ReadToEnd();
                        }

                        bool hasRazerHeadset = content.IndexOf("BlackShark", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               content.IndexOf("Barracuda", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               content.IndexOf("Kraken", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               content.IndexOf("Hammerhead", StringComparison.OrdinalIgnoreCase) >= 0;

                        bool hasRazerMouse = content.IndexOf("DeathAdder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             content.IndexOf("Viper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             content.IndexOf("Basilisk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             content.IndexOf("Cobra", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             content.IndexOf("Naga", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             content.IndexOf("Orochi", StringComparison.OrdinalIgnoreCase) >= 0;

                        if ((!forAudio && hasRazerMouse) || (forAudio && hasRazerHeadset))
                        {
                            AssignRazerModelName(content, forAudio, ref status);

                            int idx = content.LastIndexOf("powerStatus", StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                string snippet = content.Substring(idx, Math.Min(180, content.Length - idx));
                                var mLevel = Regex.Match(snippet, @"level[^\d]*(\d+)");
                                var mCharge = Regex.Match(snippet, @"chargingStatus[\\" + "\":\\s]+([^\\\",}]+)");
                                if (mLevel.Success)
                                {
                                    int lvl;
                                    if (int.TryParse(mLevel.Groups[1].Value, out lvl) && lvl > 0 && lvl <= 100)
                                    {
                                        status.Online = true;
                                        status.Level = lvl;
                                        if (mCharge.Success)
                                        {
                                            string cs = mCharge.Groups[1].Value;
                                            if (cs != "off" && cs != "NoCharge" && cs != "NoCharge_BatteryFull")
                                                status.Charging = true;
                                        }
                                        return status;
                                    }
                                }
                            }
                        }
                    }
                }

                // 2. Check Razer Synapse 3 Logs (%LOCALAPPDATA%\Razer\Synapse3\Log)
                string logDir3 = Path.Combine(localApp, @"Razer\Synapse3\Log");
                if (Directory.Exists(logDir3))
                {
                    var dirInfo3 = new DirectoryInfo(logDir3);
                    FileInfo[] files3 = dirInfo3.GetFiles("*.log");
                    foreach (var f in files3)
                    {
                        if (f.Length == 0 || f.Length > 15 * 1024 * 1024) continue;
                        string content3;
                        using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            content3 = sr.ReadToEnd();
                        }

                        var m3 = Regex.Matches(content3, @"(?:BatteryPercentage|batteryLevel|Percentage)[^\d]{1,25}(\d{1,3})", RegexOptions.IgnoreCase);
                        if (m3.Count > 0)
                        {
                            bool hasHeadset3 = content3.IndexOf("BlackShark", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               content3.IndexOf("Barracuda", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               content3.IndexOf("Kraken", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (forAudio && !hasHeadset3) continue;

                            int lvl3;
                            if (int.TryParse(m3[m3.Count - 1].Groups[1].Value, out lvl3) && lvl3 > 0 && lvl3 <= 100)
                            {
                                AssignRazerModelName(content3, forAudio, ref status);
                                status.Online = true;
                                status.Level = lvl3;
                                return status;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("WARN", "Razer query error: " + ex.Message);
            }
            return status;
        }

        private static void AssignRazerModelName(string content, bool forAudio, ref DeviceStatus status)
        {
            if (!forAudio)
            {
                if (content.IndexOf("DeathAdder V3", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "DeathAdder"; status.FullName = "Razer DeathAdder V3"; }
                else if (content.IndexOf("DeathAdder V2", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "DeathAdder"; status.FullName = "Razer DeathAdder V2 Pro"; }
                else if (content.IndexOf("Viper V3", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "Viper V3"; status.FullName = "Razer Viper V3 Pro"; }
                else if (content.IndexOf("Viper V2", StringComparison.OrdinalIgnoreCase) >= 0 || content.IndexOf("Viper Ultimate", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "Razer Viper"; status.FullName = "Razer Viper Wireless"; }
                else if (content.IndexOf("Basilisk V3", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "Basilisk V3"; status.FullName = "Razer Basilisk V3 Pro"; }
                else if (content.IndexOf("Cobra Pro", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "Cobra Pro"; status.FullName = "Razer Cobra Pro"; }
                else if (content.IndexOf("Orochi", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "Orochi V2"; status.FullName = "Razer Orochi V2"; }
                else if (content.IndexOf("Naga", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "Razer Naga"; status.FullName = "Razer Naga Wireless"; }
            }
            else
            {
                if (content.IndexOf("BlackShark", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "BlackShark"; status.FullName = "Razer BlackShark V2 Pro"; }
                else if (content.IndexOf("Barracuda", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "Barracuda"; status.FullName = "Razer Barracuda Wireless"; }
                else if (content.IndexOf("Kraken", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "Kraken WL"; status.FullName = "Razer Kraken V3 Pro"; }
                else if (content.IndexOf("Hammerhead", StringComparison.OrdinalIgnoreCase) >= 0) { status.ShortName = "Hammerhead"; status.FullName = "Razer Hammerhead Wireless"; }
            }
        }

        // ------------------------------------------------------------------
        // UNIFIED MULTI-BRAND DISPATCHERS (Mouse Slot & Headset/Audio Slot)
        // Remembers the user's detected device name when turned off, and
        // holds a 180-second anti-flicker grace window against transient
        // wireless micro-sleep / DB lock hiccups!
        // ------------------------------------------------------------------
        private static DeviceStatus _lastGoodMouse = null;
        private static DateTime _lastGoodMouseAt = DateTime.MinValue;
        private static DeviceStatus _lastGoodAudio = null;
        private static DateTime _lastGoodAudioAt = DateTime.MinValue;

        private static DeviceStatus RememberMouse(DeviceStatus s)
        {
            _lastMouseShort = s.ShortName;
            _lastMouseFull = s.FullName;
            _lastGoodMouse = s;
            _lastGoodMouseAt = DateTime.UtcNow;
            return s;
        }

        private static DeviceStatus RememberAudio(DeviceStatus s)
        {
            _lastAudioShort = s.ShortName;
            _lastAudioFull = s.FullName;
            _lastGoodAudio = s;
            _lastGoodAudioAt = DateTime.UtcNow;
            return s;
        }

        public static DeviceStatus GetUniversalMouseStatus()
        {
            var rz = GetRazerStatus(false);
            if (rz.Online && rz.Level >= 0) return RememberMouse(rz);

            var lg = GetLogitechGHubStatus(false);
            if (lg.Online && lg.Level >= 0) return RememberMouse(lg);

            var bt = GetWindowsPnpBatteryStatus(false);
            if (bt.Online && bt.Level >= 0) return RememberMouse(bt);

            var hidpp = GetLogitechHidPpUsbStatus(false);
            if (hidpp.Online && hidpp.Level >= 0) return RememberMouse(hidpp);

            // Anti-flicker grace period (up to 180s) for wireless mice entering micro-sleep or DB locks
            if (_lastGoodMouse != null && (DateTime.UtcNow - _lastGoodMouseAt).TotalSeconds < 180.0)
            {
                return _lastGoodMouse;
            }

            return new DeviceStatus
            {
                Online = false,
                Level = -1,
                Charging = false,
                ShortName = _lastMouseShort,
                FullName = _lastMouseFull,
                MaxHours = 90.0,
                ChargeMinsPerPct = 1.0
            };
        }

        public static DeviceStatus GetUniversalHeadsetStatus()
        {
            var sn = GetSonyInzoneStatus();
            if (sn.Online && sn.Level >= 0) return RememberAudio(sn);

            var lg = GetLogitechGHubStatus(true);
            if (lg.Online && lg.Level >= 0) return RememberAudio(lg);

            var rz = GetRazerStatus(true);
            if (rz.Online && rz.Level >= 0) return RememberAudio(rz);

            var bt = GetWindowsPnpBatteryStatus(true);
            if (bt.Online && bt.Level >= 0) return RememberAudio(bt);

            var usbAud = GetUsbAudioDongleFallback();
            if (usbAud.Online && (usbAud.Level >= 0 || usbAud.NoLevel)) return RememberAudio(usbAud);

            if (_lastGoodAudio != null && (DateTime.UtcNow - _lastGoodAudioAt).TotalSeconds < 60.0)
            {
                return _lastGoodAudio;
            }

            return new DeviceStatus
            {
                Online = false,
                Level = -1,
                Charging = false,
                ShortName = _lastAudioShort,
                FullName = _lastAudioFull,
                MaxHours = 30.0,
                ChargeMinsPerPct = 1.5
            };
        }

        // Window Management APIs
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public static void SetClickThrough(IntPtr hWnd, bool enable)
        {
            if (hWnd == IntPtr.Zero) return;
            int GWL_EXSTYLE = -20;
            int WS_EX_TRANSPARENT = 0x00000020;
            int WS_EX_LAYERED = 0x00080000;
            int WS_EX_TOOLWINDOW = 0x00000080;
            int style = GetWindowLong(hWnd, GWL_EXSTYLE);
            if (enable)
                SetWindowLong(hWnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
            else
                SetWindowLong(hWnd, GWL_EXSTYLE, (style & ~WS_EX_TRANSPARENT) | WS_EX_TOOLWINDOW);
        }

        public static bool EvaluateForeground(IntPtr selfHwnd, out string reason, out string winInfo)
        {
            reason = "";
            winInfo = "";

            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero || hWnd == selfHwnd)
            {
                reason = "Self or Null foreground (Desktop/Idle)";
                return false;
            }

            StringBuilder clsBuf = new StringBuilder(256);
            GetClassName(hWnd, clsBuf, 256);
            string cls = clsBuf.ToString();

            StringBuilder titleBuf = new StringBuilder(256);
            GetWindowText(hWnd, titleBuf, 256);
            string title = titleBuf.ToString();

            uint pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);

            winInfo = string.Format("PID={0}, Class='{1}', Title='{2}'", pid, cls, title);

            if (cls == "Progman" || cls == "WorkerW" || cls == "SHELLDLL_DefView" ||
                cls == "SysListView32" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ||
                cls.StartsWith("Windows.UI") || cls.Contains("ExplorerHost"))
            {
                reason = "Desktop/Shell active";
                return false;
            }

            try
            {
                var proc = Process.GetProcessById((int)pid);
                string procName = proc.ProcessName.ToLowerInvariant();
                if (procName.Contains("wallpaper") || procName == "explorer")
                {
                    reason = "Wallpaper Engine / Explorer active";
                    return false;
                }
            }
            catch {}

            if (IsZoomed(hWnd))
            {
                reason = "Window is Maximized";
                return true;
            }

            RECT r;
            if (GetWindowRect(hWnd, out r))
            {
                IntPtr hMon = MonitorFromWindow(hWnd, 2);
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(mi);
                if (GetMonitorInfo(hMon, ref mi))
                {
                    if (r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                        r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom)
                    {
                        reason = string.Format("Fullscreen [{0},{1} - {2},{3}]", r.Left, r.Top, r.Right, r.Bottom);
                        return true;
                    }
                    if (r.Left <= mi.rcWork.Left && r.Top <= mi.rcWork.Top &&
                        r.Right >= mi.rcWork.Right && r.Bottom >= mi.rcWork.Bottom)
                    {
                        reason = string.Format("Borderless Max [{0},{1} - {2},{3}]", r.Left, r.Top, r.Right, r.Bottom);
                        return true;
                    }
                }
            }

            reason = "Normal window";
            return false;
        }
    }

    public class MainWidgetApp
    {
        // Localized UI Strings (Pure ASCII Unicode Escapes)
        private const string S_Refresh        = "\u4ECA\u3059\u3050\u66F4\u65B0";
        private const string S_SizeMenu       = "\u30B5\u30A4\u30BA\u5909\u66F4 (\u30DB\u30A4\u30FC\u30EB\u3067\u3082\u53EF)";
        private const string S_SizeTiny       = "\u6975\u5C0F (60%)";
        private const string S_SizeSmall      = "\u5C0F (80%)";
        private const string S_SizeNormal     = "\u6A19\u6E96 (100%)";
        private const string S_SizeLarge      = "\u5927 (125%)";
        private const string S_ThemeMenu      = "\u30AB\u30E9\u30FC\u30C6\u30FC\u30DE";
        private const string S_ThemeAuto      = "\u81EA\u52D5 (\u6B8B\u91CF\u3067\u7DD1/\u9EC4/\u8D64)";
        private const string S_ThemeGreen     = "Razer \u30B0\u30EA\u30FC\u30F3";
        private const string S_ThemeCyan      = "\u30B5\u30A4\u30D0\u30FC\u30D6\u30EB\u30FC";
        private const string S_ThemePurple    = "\u30CD\u30AA\u30F3\u30D1\u30FC\u30D7\u30EB";
        private const string S_ThemeRed       = "\u30AF\u30EA\u30E0\u30BE\u30F3\u30EC\u30C3\u30C9";
        private const string S_ThemeWhite     = "\u30D4\u30E5\u30A2\u30DB\u30EF\u30A4\u30C8";
        private const string S_ThemeAmber     = "\u30B4\u30FC\u30EB\u30C9\u30A2\u30F3\u30D0\u30FC";
        private const string S_BgMenu         = "\u80CC\u666F\u306E\u900F\u660E\u5EA6";
        private const string S_BgDark         = "\u6A19\u6E96\u30C0\u30FC\u30AF (90%)";
        private const string S_BgGlass        = "\u534A\u900F\u660E\u30AC\u30E9\u30B9 (60%)";
        private const string S_BgLight        = "\u3046\u3063\u3059\u3089 (30%)";
        private const string S_BgTrans        = "\u5B8C\u5168\u900F\u904E\u30FB\u30EA\u30F3\u30B0\u306E\u307F (0%)";
        private const string S_BgColorMenu    = "\u80CC\u666F\u30AB\u30E9\u30FC\u5909\u66F4";
        private const string S_BgColBlack     = "\u30AA\u30CB\u30AD\u30B9\u30D6\u30E9\u30C3\u30AF (\u6A19\u6E96)";
        private const string S_BgColNavy      = "\u30DF\u30C3\u30C9\u30CA\u30A4\u30C8\u30CD\u30A4\u30D3\u30FC";
        private const string S_BgColPurple    = "\u30B5\u30A4\u30D0\u30FC\u30D1\u30FC\u30D7\u30EB";
        private const string S_BgColForest    = "\u30D5\u30A9\u30EC\u30B9\u30C8\u30B0\u30EA\u30FC\u30F3";
        private const string S_BgColWine      = "\u30EF\u30A4\u30F3\u30EC\u30C3\u30C9";
        private const string S_BgColCharcoal  = "\u30B9\u30C1\u30FC\u30EB\u30B0\u30EC\u30FC";
        private const string S_BgColCustom    = "\u81EA\u7531\u306B\u8272\u3092\u9078\u3076 (\u30AB\u30E9\u30FC\u30D1\u30EC\u30C3\u30C8)...";
        private const string S_BgImgMenu      = "\u80CC\u666F\u753B\u50CF (\u63A8\u3057\u30FB\u58C1\u7D19)";
        private const string S_BgImgSelect    = "\u753B\u50CF\u30D5\u30A1\u30A4\u30EB\u3092\u8AAD\u307F\u8FBC\u3080...";
        private const string S_BgImgClear     = "\u80CC\u666F\u753B\u50CF\u3092\u30AA\u30D5\u306B\u3059\u308B";
        private const string S_LockPos        = "\u4F4D\u7F6E\u30FB\u30B5\u30A4\u30BA\u3092\u30ED\u30C3\u30AF";
        private const string S_GhostMode      = "\u30AF\u30EA\u30C3\u30AF\u3059\u308A\u629C\u3051 (\u30B4\u30FC\u30B9\u30C8\u30E2\u30FC\u30C9)";
        private const string S_AutoHide       = "\u5168\u753B\u9762\u30FB\u6700\u5927\u5316\u6642\u306F\u81EA\u52D5\u3067\u96A0\u3059";
        private const string S_LowBatNotify   = "\u4F4E\u30D0\u30C3\u30C6\u30EA\u30FC\u901A\u77E5 (20%\u4EE5\u4E0B)";
        private const string S_OpenLog        = "\u30C7\u30D0\u30C3\u30B0\u30ED\u30B0\u3092\u958B\u304F (\u30E1\u30E2\u5E33)";
        private const string S_AutoStart      = "Windows \u8D77\u52D5\u6642\u306B\u81EA\u52D5\u8D77\u52D5";
        private const string S_Exit           = "\u7D42\u4E86";
        private const string S_Offline        = "\u30AA\u30D5";
        private const string S_NotifyTitle    = "\u30D0\u30C3\u30C6\u30EA\u30FC\u6B8B\u91CF\u4F4E\u4E0B";
        private const string S_NotifyBodySuf  = " \u306E\u30D0\u30C3\u30C6\u30EA\u30FC\u304C\u6B8B\u308A\u5C11\u306A\u304F\u306A\u3063\u3066\u3044\u307E\u3059\u3002\u5145\u96FB\u3092\u304A\u3059\u3059\u3081\u3057\u307E\u3059\u3002";
        private const string S_GhostTitle     = "\u30B4\u30FC\u30B9\u30C8\u30E2\u30FC\u30C9\u6709\u52B9";
        private const string S_GhostBody      = "\u30AF\u30EA\u30C3\u30AF\u3059\u308A\u629C\u3051\u304CON\u306B\u306A\u308A\u307E\u3057\u305F\u3002\u89E3\u9664\u306F\u30BF\u30B9\u30AF\u30C8\u30EC\u30A4(\u53F3\u4E0B)\u306E\u30A2\u30A4\u30B3\u30F3\u304B\u3089\u884C\u3048\u307E\u3059\u3002";
        private const string S_TipCharging    = "\u72B6\u614B: \u26A1 \u5145\u96FB\u4E2D";
        private const string S_TipDischarging = "\u72B6\u614B: \u4F7F\u7528\u4E2D (\u653E\u96FB)";
        private const string S_TipOffline     = "\u72B6\u614B: \u30AA\u30D5\u30E9\u30A4\u30F3 / \u30B9\u30EA\u30FC\u30D7";
        private const string S_TipRemainPref  = "\u63A8\u5B9A\u6B8B\u308A\u6642\u9593: \u7D04 ";
        private const string S_TipFullPref    = "\u6E80\u5145\u96FB\u307E\u3067: \u7D04 ";
        private const string S_TipHours       = " \u6642\u9593";
        private const string S_TipMins        = " \u5206";
        private const string S_TipFullDone    = "\u72B6\u614B: \u26A1 \u5145\u96FB\u5B8C\u4E86 (100%)";
        private const string S_TrayTip        = "Ring Battery Monitor (\u53F3\u30AF\u30EA\u30C3\u30AF\u3067\u8A2D\u5B9A)";

        private string _appDir;
        private string _configPath;
        private string _startupLnkPath;
        private WidgetSettings _settings;

        private Window _window;
        private IntPtr _hwnd = IntPtr.Zero;
        private Border _rootBorder;
        private Border _bgImageLayer;
        private DropShadowEffect _rootShadow;
        private ScaleTransform _widgetScale;
        private Grid _headerGrid;
        private TextBlock _headerBolt;
        private Button _btnClose;
        private Border _sepLine;

        private System.Windows.Shapes.Path _pathMouseArc;
        private TextBlock _txtMouseLevel;
        private TextBlock _txtMouseCharging;
        private TextBlock _txtMouseLabel;
        private TextBlock _tipMouseTitle;
        private TextBlock _tipMouseStatus;
        private TextBlock _tipMouseTime;

        private System.Windows.Shapes.Path _pathHeadsetArc;
        private TextBlock _txtHeadsetLevel;
        private TextBlock _txtHeadsetCharging;
        private TextBlock _txtHeadsetLabel;
        private TextBlock _tipHeadsetTitle;
        private TextBlock _tipHeadsetStatus;
        private TextBlock _tipHeadsetTime;

        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private System.Windows.Forms.ToolStripMenuItem _trayMenuGhost;
        private System.Windows.Forms.ToolStripMenuItem _trayMenuLock;

        private ContextMenu _contextMenu;
        private MenuItem _menuLockPos;
        private MenuItem _menuGhost;
        private Dictionary<string, MenuItem> _themeMenuItems = new Dictionary<string, MenuItem>();
        private Dictionary<string, MenuItem> _bgMenuItems = new Dictionary<string, MenuItem>();
        private Dictionary<string, MenuItem> _bgColorMenuItems = new Dictionary<string, MenuItem>();
        private MenuItem _menuBgImgClear;

        private Dictionary<string, string> _themeHexMap = new Dictionary<string, string>
        {
            { "Green",  "#10B981" },
            { "Cyan",   "#06B6D4" },
            { "Purple", "#A855F7" },
            { "Red",    "#F43F5E" },
            { "White",  "#F4F4F5" },
            { "Amber",  "#F59E0B" }
        };

        private bool _mouseLowNotified = false;
        private bool _headsetLowNotified = false;
        private bool _lastHideState = false;
        private int _tickCount = 0;
        private BrushConverter _brushConv = new BrushConverter();

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\RingBatteryMonitor_Standalone_Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    // Another instance of RingBatteryMonitor.exe is already running; exit cleanly
                    return;
                }
                var app = new MainWidgetApp();
                app.Run();
            }
        }

        private SolidColorBrush GetBrush(string hex)
        {
            return (SolidColorBrush)_brushConv.ConvertFromString(hex);
        }

        public void Run()
        {
            _appDir = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = System.IO.Path.Combine(_appDir, "widget_settings.json");
            NativeEngine.LogFilePath = System.IO.Path.Combine(_appDir, "debug.log");
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            _startupLnkPath = System.IO.Path.Combine(startupFolder, "Ring Battery Monitor.lnk");

            NativeEngine.Log("INFO", "=== Ring Battery Monitor v" + Updater.CurrentText + " Starting ===");
            _settings = WidgetSettings.Load(_configPath);
            NativeEngine.LogHidInventory();

            InitTrayIcon();
            InitWindow();
            InitMenus();

            ApplyBgOpacityMode(_settings.BgOpacityMode);
            ApplyLockPosition(_settings.LockPosition);
            ApplyColorTheme(_settings.ColorTheme);

            _window.SourceInitialized += (s, e) =>
            {
                var helper = new WindowInteropHelper(_window);
                _hwnd = helper.Handle;
                NativeEngine.SetClickThrough(_hwnd, _settings.ClickThrough);
                NativeEngine.Log("INFO", "Window initialized. Handle: " + _hwnd);
            };

            UpdateBatteryDisplay();

            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(300);
            timer.Tick += OnTimerTick;
            timer.Start();

            StartUpdateChecks();

            _window.Show();
            Dispatcher.Run();
        }

        private System.Windows.Forms.ToolStripMenuItem _trayUpdate;
        private string _updateTag;
        private string _updateUrl;

        private void StartUpdateChecks()
        {
            if (string.IsNullOrEmpty(Updater.Repo)) return;
            Action check = () =>
            {
                if (!_settings.AutoUpdateCheck) return;
                ThreadPool.QueueUserWorkItem(o =>
                {
                    string tag, url;
                    if (!Updater.TryCheck(out tag, out url)) return;
                    _window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_updateTag == tag) return;
                        _updateTag = tag;
                        _updateUrl = url;
                        _trayUpdate.Text = "Update to " + tag;
                        _trayUpdate.Visible = true;
                        _notifyIcon.ShowBalloonTip(5000, "Ring Battery Monitor", "New version " + tag + " is available (tray menu)", System.Windows.Forms.ToolTipIcon.Info);
                    }));
                });
            };
            check();
            var t = new DispatcherTimer();
            t.Interval = TimeSpan.FromHours(6);
            t.Tick += (s, e) => check();
            t.Start();
        }

        private void InitTrayIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            var bmp = new System.Drawing.Bitmap(16, 16);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(16, 185, 129), 2.2f))
                {
                    g.DrawEllipse(pen, 2, 2, 11, 11);
                }
                using (var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(16, 185, 129)))
                {
                    g.FillEllipse(br, 6, 6, 4, 4);
                }
            }
            _notifyIcon.Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
            _notifyIcon.Text = S_TrayTip;
            _notifyIcon.Visible = true;
        }

        private void InitWindow()
        {
            string xaml = @"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        Title=""Ring Battery Monitor""
        SizeToContent=""WidthAndHeight""
        WindowStyle=""None""
        AllowsTransparency=""True""
        Background=""Transparent""
        Topmost=""True""
        ShowInTaskbar=""False""
        ResizeMode=""NoResize"">
    
    <Border Name=""RootBorder""
            Background=""#E618181B""
            BorderBrush=""#3F3F46""
            BorderThickness=""1""
            CornerRadius=""16""
            Margin=""6"">
        <Border.Effect>
            <DropShadowEffect x:Name=""RootShadow"" Color=""#000000"" BlurRadius=""12"" ShadowDepth=""3"" Opacity=""0.5""/>
        </Border.Effect>
        <Border.LayoutTransform>
            <ScaleTransform x:Name=""WidgetScale"" ScaleX=""1.0"" ScaleY=""1.0""/>
        </Border.LayoutTransform>

        <Grid>
            <Border Name=""BgImageLayer"" CornerRadius=""15"" Visibility=""Collapsed"" IsHitTestVisible=""False""/>
            <StackPanel Margin=""10,7,10,9"">
                <Grid Name=""HeaderGrid"" Margin=""0,0,0,5"">
                    <StackPanel Orientation=""Horizontal"" VerticalAlignment=""Center"">
                        <TextBlock Name=""HeaderBolt"" Text=""&#x26A1;"" FontSize=""10"" Foreground=""#10B981"" Margin=""0,0,3,0""/>
                        <TextBlock Name=""HeaderTitle"" Text=""BATTERY"" FontSize=""9"" FontWeight=""Bold"" Foreground=""#71717A"" VerticalAlignment=""Center""/>
                    </StackPanel>
                    <Button Name=""BtnClose"" Content=""&#x2715;"" Width=""16"" Height=""16""
                            HorizontalAlignment=""Right"" VerticalAlignment=""Center""
                            Background=""Transparent"" Foreground=""#71717A"" BorderThickness=""0""
                            FontSize=""10"" Cursor=""Hand"" ToolTip=""Close""/>
                </Grid>

                <StackPanel Orientation=""Horizontal"" HorizontalAlignment=""Center"">
                    <StackPanel Name=""PanelMouse"" Margin=""2,0,6,0"" HorizontalAlignment=""Center"" Background=""#01000000"">
                        <StackPanel.ToolTip>
                            <ToolTip Background=""#F218181B"" BorderBrush=""#3F3F46"" Foreground=""#F4F4F5"" Padding=""10,8"">
                                <StackPanel>
                                    <TextBlock Name=""TipMouseTitle"" Text=""&#x1F5B1; Razer Wireless Mouse"" FontWeight=""Bold"" FontSize=""11.5"" Margin=""0,0,0,4""/>
                                    <TextBlock Name=""TipMouseStatus"" Text=""--"" FontSize=""11"" Foreground=""#A1A1AA""/>
                                    <TextBlock Name=""TipMouseTime"" Text=""--"" FontSize=""11"" Foreground=""#10B981"" FontWeight=""SemiBold"" Margin=""0,2,0,0""/>
                                </StackPanel>
                            </ToolTip>
                        </StackPanel.ToolTip>
                        <Grid Width=""58"" Height=""58"">
                            <Ellipse Width=""50"" Height=""50"" Stroke=""#27272A"" StrokeThickness=""5""/>
                            <Path Name=""PathMouseArc"" Stroke=""#10B981"" StrokeThickness=""5""
                                  StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round""/>
                            <StackPanel VerticalAlignment=""Center"" HorizontalAlignment=""Center"">
                                <StackPanel Orientation=""Horizontal"" HorizontalAlignment=""Center"">
                                    <TextBlock Text=""&#x1F5B1;"" FontSize=""11"" Foreground=""#E4E4E7""/>
                                    <TextBlock Name=""TxtMouseCharging"" Text=""&#x26A1;"" FontSize=""10"" Foreground=""#FBBF24"" Margin=""1,-2,0,0"" Visibility=""Collapsed""/>
                                </StackPanel>
                                <TextBlock Name=""TxtMouseLevel"" Text=""--"" FontSize=""10"" FontWeight=""Bold"" Foreground=""#10B981"" HorizontalAlignment=""Center""/>
                            </StackPanel>
                        </Grid>
                        <TextBlock Name=""TxtMouseLabel"" Text=""DeathAdder"" FontSize=""9"" Foreground=""#A1A1AA"" FontWeight=""SemiBold"" HorizontalAlignment=""Center"" Margin=""0,3,0,0""/>
                    </StackPanel>

                    <Border Name=""SepLine"" Width=""1"" Background=""#27272A"" Height=""52"" VerticalAlignment=""Center"" Margin=""0,0,4,0""/>

                    <StackPanel Name=""PanelHeadset"" Margin=""2,0,2,0"" HorizontalAlignment=""Center"" Background=""#01000000"">
                        <StackPanel.ToolTip>
                            <ToolTip Background=""#F218181B"" BorderBrush=""#3F3F46"" Foreground=""#F4F4F5"" Padding=""10,8"">
                                <StackPanel>
                                    <TextBlock Name=""TipHeadsetTitle"" Text=""&#x1F3A7; SONY INZONE Wireless"" FontWeight=""Bold"" FontSize=""11.5"" Margin=""0,0,0,4""/>
                                    <TextBlock Name=""TipHeadsetStatus"" Text=""--"" FontSize=""11"" Foreground=""#A1A1AA""/>
                                    <TextBlock Name=""TipHeadsetTime"" Text=""--"" FontSize=""11"" Foreground=""#10B981"" FontWeight=""SemiBold"" Margin=""0,2,0,0""/>
                                </StackPanel>
                            </ToolTip>
                        </StackPanel.ToolTip>
                        <Grid Width=""58"" Height=""58"">
                            <Ellipse Width=""50"" Height=""50"" Stroke=""#27272A"" StrokeThickness=""5""/>
                            <Path Name=""PathHeadsetArc"" Stroke=""#10B981"" StrokeThickness=""5""
                                  StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round""/>
                            <StackPanel VerticalAlignment=""Center"" HorizontalAlignment=""Center"">
                                <StackPanel Orientation=""Horizontal"" HorizontalAlignment=""Center"">
                                    <TextBlock Text=""&#x1F3A7;"" FontSize=""11"" Foreground=""#E4E4E7""/>
                                    <TextBlock Name=""TxtHeadsetCharging"" Text=""&#x26A1;"" FontSize=""10"" Foreground=""#FBBF24"" Margin=""1,-2,0,0"" Visibility=""Collapsed""/>
                                </StackPanel>
                                <TextBlock Name=""TxtHeadsetLevel"" Text=""--"" FontSize=""10"" FontWeight=""Bold"" Foreground=""#10B981"" HorizontalAlignment=""Center""/>
                            </StackPanel>
                        </Grid>
                        <TextBlock Name=""TxtHeadsetLabel"" Text=""INZONE H5"" FontSize=""9"" Foreground=""#A1A1AA"" FontWeight=""SemiBold"" HorizontalAlignment=""Center"" Margin=""0,3,0,0""/>
                    </StackPanel>
                </StackPanel>
            </StackPanel>
        </Grid>
    </Border>
</Window>";

            using (var sr = new StringReader(xaml))
            using (var xr = System.Xml.XmlReader.Create(sr))
            {
                _window = (Window)XamlReader.Load(xr);
            }

            _rootBorder = (Border)_window.FindName("RootBorder");
            _bgImageLayer = (Border)_window.FindName("BgImageLayer");
            _rootShadow = (DropShadowEffect)_window.FindName("RootShadow");
            _widgetScale = (ScaleTransform)_window.FindName("WidgetScale");
            _headerGrid = (Grid)_window.FindName("HeaderGrid");
            _headerBolt = (TextBlock)_window.FindName("HeaderBolt");
            _btnClose = (Button)_window.FindName("BtnClose");
            _sepLine = (Border)_window.FindName("SepLine");

            _pathMouseArc = (System.Windows.Shapes.Path)_window.FindName("PathMouseArc");
            _txtMouseLevel = (TextBlock)_window.FindName("TxtMouseLevel");
            _txtMouseCharging = (TextBlock)_window.FindName("TxtMouseCharging");
            _txtMouseLabel = (TextBlock)_window.FindName("TxtMouseLabel");
            _tipMouseTitle = (TextBlock)_window.FindName("TipMouseTitle");
            _tipMouseStatus = (TextBlock)_window.FindName("TipMouseStatus");
            _tipMouseTime = (TextBlock)_window.FindName("TipMouseTime");

            _pathHeadsetArc = (System.Windows.Shapes.Path)_window.FindName("PathHeadsetArc");
            _txtHeadsetLevel = (TextBlock)_window.FindName("TxtHeadsetLevel");
            _txtHeadsetCharging = (TextBlock)_window.FindName("TxtHeadsetCharging");
            _txtHeadsetLabel = (TextBlock)_window.FindName("TxtHeadsetLabel");
            _tipHeadsetTitle = (TextBlock)_window.FindName("TipHeadsetTitle");
            _tipHeadsetStatus = (TextBlock)_window.FindName("TipHeadsetStatus");
            _tipHeadsetTime = (TextBlock)_window.FindName("TipHeadsetTime");

            _widgetScale.ScaleX = _settings.Scale;
            _widgetScale.ScaleY = _settings.Scale;
            _window.Left = _settings.Left;
            _window.Top = _settings.Top;

            _window.MouseEnter += (s, e) =>
            {
                if (_settings.BgOpacityMode == "trans") _headerGrid.Opacity = 0.9;
            };
            _window.MouseLeave += (s, e) =>
            {
                if (_settings.BgOpacityMode == "trans") _headerGrid.Opacity = 0.0;
            };

            _window.MouseLeftButtonDown += (s, e) =>
            {
                if (!_settings.LockPosition)
                {
                    try { _window.DragMove(); } catch {}
                    SaveCurrentSettings();
                }
            };

            _window.LocationChanged += (s, e) => SaveCurrentSettings();

            _btnClose.Click += (s, e) => ShutdownApp();

            _window.MouseWheel += (s, e) =>
            {
                if (_settings.LockPosition) return;
                double sc = _widgetScale.ScaleX;
                if (e.Delta > 0) sc = Math.Min(1.6, sc + 0.05);
                else sc = Math.Max(0.5, sc - 0.05);
                _widgetScale.ScaleX = sc;
                _widgetScale.ScaleY = sc;
                SaveCurrentSettings();
            };
        }

        private void InitMenus()
        {
            _contextMenu = new ContextMenu();

            var menuRefresh = new MenuItem { Header = S_Refresh };
            menuRefresh.Click += (s, e) => UpdateBatteryDisplay();
            _contextMenu.Items.Add(menuRefresh);

            var menuSize = new MenuItem { Header = S_SizeMenu };
            AddSizePreset(menuSize, S_SizeTiny, 0.6);
            AddSizePreset(menuSize, S_SizeSmall, 0.8);
            AddSizePreset(menuSize, S_SizeNormal, 1.0);
            AddSizePreset(menuSize, S_SizeLarge, 1.25);
            _contextMenu.Items.Add(menuSize);

            var menuTheme = new MenuItem { Header = S_ThemeMenu };
            AddThemeOption(menuTheme, "Auto", S_ThemeAuto);
            AddThemeOption(menuTheme, "Green", S_ThemeGreen);
            AddThemeOption(menuTheme, "Cyan", S_ThemeCyan);
            AddThemeOption(menuTheme, "Purple", S_ThemePurple);
            AddThemeOption(menuTheme, "Red", S_ThemeRed);
            AddThemeOption(menuTheme, "White", S_ThemeWhite);
            AddThemeOption(menuTheme, "Amber", S_ThemeAmber);
            _contextMenu.Items.Add(menuTheme);

            var menuBg = new MenuItem { Header = S_BgMenu };
            AddBgOption(menuBg, "dark", S_BgDark);
            AddBgOption(menuBg, "glass", S_BgGlass);
            AddBgOption(menuBg, "light", S_BgLight);
            AddBgOption(menuBg, "trans", S_BgTrans);
            _contextMenu.Items.Add(menuBg);

            var menuBgColor = new MenuItem { Header = S_BgColorMenu };
            AddBgColorOption(menuBgColor, "#18181B", S_BgColBlack);
            AddBgColorOption(menuBgColor, "#0F172A", S_BgColNavy);
            AddBgColorOption(menuBgColor, "#1E1B4B", S_BgColPurple);
            AddBgColorOption(menuBgColor, "#052E16", S_BgColForest);
            AddBgColorOption(menuBgColor, "#3B071E", S_BgColWine);
            AddBgColorOption(menuBgColor, "#27272A", S_BgColCharcoal);
            menuBgColor.Items.Add(new Separator());
            var itemCustomCol = new MenuItem { Header = S_BgColCustom };
            itemCustomCol.Click += (s, e) => ChooseCustomBgColor();
            menuBgColor.Items.Add(itemCustomCol);
            _contextMenu.Items.Add(menuBgColor);

            var menuBgImg = new MenuItem { Header = S_BgImgMenu };
            var itemImgSelect = new MenuItem { Header = S_BgImgSelect };
            itemImgSelect.Click += (s, e) => SelectBgImageFile();
            menuBgImg.Items.Add(itemImgSelect);
            _menuBgImgClear = new MenuItem { Header = S_BgImgClear, IsEnabled = !string.IsNullOrEmpty(_settings.BgImagePath) };
            _menuBgImgClear.Click += (s, e) => ClearBgImage();
            menuBgImg.Items.Add(_menuBgImgClear);
            _contextMenu.Items.Add(menuBgImg);

            _contextMenu.Items.Add(new Separator());

            _menuLockPos = new MenuItem { Header = S_LockPos, IsCheckable = true, IsChecked = _settings.LockPosition };
            _menuLockPos.Click += (s, e) => ApplyLockPosition(!_settings.LockPosition);
            _contextMenu.Items.Add(_menuLockPos);

            _menuGhost = new MenuItem { Header = S_GhostMode, IsCheckable = true, IsChecked = _settings.ClickThrough };
            _menuGhost.Click += (s, e) => ApplyClickThrough(!_settings.ClickThrough, true);
            _contextMenu.Items.Add(_menuGhost);

            var menuAutoHide = new MenuItem { Header = S_AutoHide, IsCheckable = true, IsChecked = _settings.AutoHideFullScreen };
            menuAutoHide.Click += (s, e) =>
            {
                _settings.AutoHideFullScreen = !_settings.AutoHideFullScreen;
                menuAutoHide.IsChecked = _settings.AutoHideFullScreen;
                SaveCurrentSettings();
            };
            _contextMenu.Items.Add(menuAutoHide);

            var menuLowBat = new MenuItem { Header = S_LowBatNotify, IsCheckable = true, IsChecked = _settings.LowBatteryNotify };
            menuLowBat.Click += (s, e) =>
            {
                _settings.LowBatteryNotify = !_settings.LowBatteryNotify;
                menuLowBat.IsChecked = _settings.LowBatteryNotify;
                SaveCurrentSettings();
            };
            _contextMenu.Items.Add(menuLowBat);

            _contextMenu.Items.Add(new Separator());

            var menuLog = new MenuItem { Header = S_OpenLog };
            menuLog.Click += (s, e) =>
            {
                try { Process.Start("notepad.exe", "\"" + NativeEngine.LogFilePath + "\""); } catch {}
            };
            _contextMenu.Items.Add(menuLog);

            var menuAutoStart = new MenuItem { Header = S_AutoStart, IsCheckable = true, IsChecked = File.Exists(_startupLnkPath) };
            menuAutoStart.Click += (s, e) =>
            {
                bool target = !File.Exists(_startupLnkPath);
                SetAutoStart(target);
                menuAutoStart.IsChecked = File.Exists(_startupLnkPath);
            };
            _contextMenu.Items.Add(menuAutoStart);

            _contextMenu.Items.Add(new Separator());

            var menuExit = new MenuItem { Header = S_Exit };
            menuExit.Click += (s, e) => ShutdownApp();
            _contextMenu.Items.Add(menuExit);

            _window.ContextMenu = _contextMenu;

            // System Tray Menu
            var trayMenu = new System.Windows.Forms.ContextMenuStrip();
            var trayRefresh = trayMenu.Items.Add(S_Refresh);
            trayRefresh.Click += (s, e) => UpdateBatteryDisplay();
            _trayUpdate = new System.Windows.Forms.ToolStripMenuItem("Update") { Visible = false };
            _trayUpdate.Click += (s, e) =>
            {
                if (_updateUrl != null && Updater.DownloadAndStage(_updateUrl)) ShutdownApp();
            };
            trayMenu.Items.Add(_trayUpdate);
            trayMenu.Items.Add("-");

            _trayMenuGhost = new System.Windows.Forms.ToolStripMenuItem(S_GhostMode) { Checked = _settings.ClickThrough };
            _trayMenuGhost.Click += (s, e) => ApplyClickThrough(!_settings.ClickThrough, false);
            trayMenu.Items.Add(_trayMenuGhost);

            _trayMenuLock = new System.Windows.Forms.ToolStripMenuItem(S_LockPos) { Checked = _settings.LockPosition };
            _trayMenuLock.Click += (s, e) => ApplyLockPosition(!_settings.LockPosition);
            trayMenu.Items.Add(_trayMenuLock);

            var trayLog = trayMenu.Items.Add(S_OpenLog);
            trayLog.Click += (s, e) =>
            {
                try { Process.Start("notepad.exe", "\"" + NativeEngine.LogFilePath + "\""); } catch {}
            };
            trayMenu.Items.Add("-");

            var trayExit = trayMenu.Items.Add(S_Exit);
            trayExit.Click += (s, e) => ShutdownApp();

            _notifyIcon.ContextMenuStrip = trayMenu;
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    if (_hwnd != IntPtr.Zero) NativeEngine.SetForegroundWindow(_hwnd);
                    _contextMenu.Placement = PlacementMode.MousePoint;
                    _contextMenu.IsOpen = true;
                }
            };
        }

        private void AddSizePreset(MenuItem parent, string title, double scaleVal)
        {
            var item = new MenuItem { Header = title };
            item.Click += (s, e) =>
            {
                _widgetScale.ScaleX = scaleVal;
                _widgetScale.ScaleY = scaleVal;
                SaveCurrentSettings();
            };
            parent.Items.Add(item);
        }

        private void AddThemeOption(MenuItem parent, string key, string title)
        {
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = (_settings.ColorTheme == key) };
            item.Click += (s, e) => ApplyColorTheme(key);
            _themeMenuItems[key] = item;
            parent.Items.Add(item);
        }

        private void AddBgOption(MenuItem parent, string key, string title)
        {
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = (_settings.BgOpacityMode == key) };
            item.Click += (s, e) => ApplyBgOpacityMode(key);
            _bgMenuItems[key] = item;
            parent.Items.Add(item);
        }

        private void AddBgColorOption(MenuItem parent, string hex, string title)
        {
            var item = new MenuItem
            {
                Header = title,
                IsCheckable = true,
                IsChecked = string.Equals(_settings.BgColor, hex, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (s, e) => ApplyBgColor(hex);
            _bgColorMenuItems[hex.ToUpperInvariant()] = item;
            parent.Items.Add(item);
        }

        private void ApplyBgColor(string hex)
        {
            if (string.IsNullOrEmpty(hex) || !hex.StartsWith("#") || hex.Length != 7)
                hex = "#18181B";
            _settings.BgColor = hex.ToUpperInvariant();
            foreach (var kv in _bgColorMenuItems)
                kv.Value.IsChecked = string.Equals(kv.Key, _settings.BgColor, StringComparison.OrdinalIgnoreCase);
            ApplyBgOpacityMode(_settings.BgOpacityMode);
        }

        private void ChooseCustomBgColor()
        {
            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.FullOpen = true;
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(_settings.BgColor);
                    dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
                }
                catch {}

                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string hex = string.Format("#{0:X2}{1:X2}{2:X2}", dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    ApplyBgColor(hex);
                }
            }
        }

        private void SelectBgImageFile()
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Title = S_BgImgMenu;
                dlg.Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files (*.*)|*.*";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _settings.BgImagePath = dlg.FileName;
                    if (_menuBgImgClear != null) _menuBgImgClear.IsEnabled = true;
                    ApplyBgOpacityMode(_settings.BgOpacityMode);
                }
            }
        }

        private void ClearBgImage()
        {
            _settings.BgImagePath = "";
            if (_menuBgImgClear != null) _menuBgImgClear.IsEnabled = false;
            ApplyBgOpacityMode(_settings.BgOpacityMode);
        }

        private void ApplyBgOpacityMode(string mode)
        {
            _settings.BgOpacityMode = mode;
            string rgb = (_settings.BgColor != null && _settings.BgColor.StartsWith("#") && _settings.BgColor.Length == 7)
                ? _settings.BgColor.Substring(1)
                : "18181B";

            double imgOpacity = 0.85;
            if (mode == "glass")
            {
                _rootBorder.Background = GetBrush("#99" + rgb);
                _rootBorder.BorderBrush = GetBrush("#333F3F46");
                _rootShadow.Opacity = 0.3;
                _sepLine.Background = GetBrush("#4427272A");
                _headerGrid.Opacity = 1.0;
                imgOpacity = 0.55;
            }
            else if (mode == "light")
            {
                _rootBorder.Background = GetBrush("#4D" + rgb);
                _rootBorder.BorderBrush = GetBrush("#1A3F3F46");
                _rootShadow.Opacity = 0.15;
                _sepLine.Background = GetBrush("#2227272A");
                _headerGrid.Opacity = 0.85;
                imgOpacity = 0.30;
            }
            else if (mode == "trans")
            {
                _rootBorder.Background = GetBrush("#01000000");
                _rootBorder.BorderBrush = GetBrush("#00000000");
                _rootShadow.Opacity = 0.0;
                _sepLine.Background = GetBrush("#00000000");
                _headerGrid.Opacity = 0.0;
                imgOpacity = 0.0;
            }
            else
            {
                _rootBorder.Background = GetBrush("#E6" + rgb);
                _rootBorder.BorderBrush = GetBrush("#3F3F46");
                _rootShadow.Opacity = 0.5;
                _sepLine.Background = GetBrush("#27272A");
                _headerGrid.Opacity = 1.0;
                imgOpacity = 0.82;
            }

            if (_bgImageLayer != null)
            {
                if (!string.IsNullOrEmpty(_settings.BgImagePath) && File.Exists(_settings.BgImagePath) && imgOpacity > 0.0)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(_settings.BgImagePath, UriKind.Absolute);
                        bmp.EndInit();
                        var ib = new ImageBrush(bmp);
                        ib.Stretch = Stretch.UniformToFill;
                        ib.Opacity = imgOpacity;
                        _bgImageLayer.Background = ib;
                        _bgImageLayer.Visibility = Visibility.Visible;
                    }
                    catch
                    {
                        _bgImageLayer.Background = null;
                        _bgImageLayer.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    _bgImageLayer.Background = null;
                    _bgImageLayer.Visibility = Visibility.Collapsed;
                }
            }

            foreach (var kv in _bgMenuItems) kv.Value.IsChecked = (kv.Key == mode);
            SaveCurrentSettings();
        }

        private void ApplyLockPosition(bool locked)
        {
            _settings.LockPosition = locked;
            _btnClose.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
            if (_menuLockPos != null) _menuLockPos.IsChecked = locked;
            if (_trayMenuLock != null) _trayMenuLock.Checked = locked;
            SaveCurrentSettings();
        }

        private void ApplyClickThrough(bool ghost, bool notifyUser)
        {
            _settings.ClickThrough = ghost;
            if (_hwnd != IntPtr.Zero) NativeEngine.SetClickThrough(_hwnd, ghost);
            if (_menuGhost != null) _menuGhost.IsChecked = ghost;
            if (_trayMenuGhost != null) _trayMenuGhost.Checked = ghost;
            if (ghost && notifyUser)
            {
                _notifyIcon.ShowBalloonTip(4000, S_GhostTitle, S_GhostBody, System.Windows.Forms.ToolTipIcon.Info);
            }
            SaveCurrentSettings();
        }

        private void ApplyColorTheme(string themeName)
        {
            _settings.ColorTheme = themeName;
            if (themeName != "Auto" && _themeHexMap.ContainsKey(themeName))
                _headerBolt.Foreground = GetBrush(_themeHexMap[themeName]);
            else
                _headerBolt.Foreground = GetBrush("#10B981");

            foreach (var kv in _themeMenuItems) kv.Value.IsChecked = (kv.Key == themeName);
            SaveCurrentSettings();
            UpdateBatteryDisplay();
        }

        private SolidColorBrush GetLevelBrush(int level)
        {
            if (_settings.ColorTheme != "Auto" && _themeHexMap.ContainsKey(_settings.ColorTheme))
            {
                if (level < 15) return GetBrush("#EF4444");
                return GetBrush(_themeHexMap[_settings.ColorTheme]);
            }
            if (level >= 60) return GetBrush("#10B981");
            if (level >= 20) return GetBrush("#F59E0B");
            return GetBrush("#EF4444");
        }

        private void SetProgressArc(System.Windows.Shapes.Path pathElem, int percent, SolidColorBrush brush)
        {
            if (percent <= 0)
            {
                pathElem.Data = null;
                return;
            }
            double radius = 25.0;
            double cx = 29.0;
            double cy = 29.0;
            double angle = (percent >= 100) ? 359.9 : Math.Max(1.0, (percent / 100.0) * 360.0);
            double startRad = -90.0 * Math.PI / 180.0;
            double endRad = (angle - 90.0) * Math.PI / 180.0;

            double sx = cx + radius * Math.Cos(startRad);
            double sy = cy + radius * Math.Sin(startRad);
            double ex = cx + radius * Math.Cos(endRad);
            double ey = cy + radius * Math.Sin(endRad);
            string largeArc = (angle > 180.0) ? "1" : "0";

            string dataStr = string.Format(CultureInfo.InvariantCulture,
                "M {0:F3},{1:F3} A {2:F3},{2:F3} 0 {3} 1 {4:F3},{5:F3}",
                sx, sy, radius, largeArc, ex, ey);

            pathElem.Data = Geometry.Parse(dataStr);
            pathElem.Stroke = brush;
        }

        private void UpdateBatteryDisplay()
        {
            var gray = GetBrush("#71717A");

            // 1. Universal Wireless Mouse (Razer / Logicool G / Bluetooth / HID)
            var rz = NativeEngine.GetUniversalMouseStatus();
            _txtMouseLabel.Text = rz.ShortName;
            _tipMouseTitle.Text = "\uD83D\uDDB1 " + rz.FullName;

            if (rz.Online && rz.Level >= 0)
            {
                _txtMouseLevel.Text = rz.Level + "%";
                var br = GetLevelBrush(rz.Level);
                _txtMouseLevel.Foreground = br;
                SetProgressArc(_pathMouseArc, rz.Level, br);
                _txtMouseCharging.Visibility = rz.Charging ? Visibility.Visible : Visibility.Collapsed;

                if (rz.Charging)
                {
                    if (rz.Level >= 100)
                    {
                        _tipMouseStatus.Text = S_TipFullDone;
                        _tipMouseTime.Text = "";
                    }
                    else
                    {
                        int mins = Math.Max(5, (int)Math.Round((100 - rz.Level) * rz.ChargeMinsPerPct));
                        _tipMouseStatus.Text = S_TipCharging + " (" + rz.Level + "%)";
                        _tipMouseTime.Text = S_TipFullPref + mins + S_TipMins;
                    }
                    _tipMouseTime.Foreground = GetBrush("#FBBF24");
                    _mouseLowNotified = false;
                }
                else
                {
                    double hrs = Math.Max(0.5, Math.Round((rz.Level / 100.0) * rz.MaxHours, 1));
                    _tipMouseStatus.Text = S_TipDischarging + " (" + rz.Level + "%)";
                    _tipMouseTime.Text = S_TipRemainPref + hrs.ToString("0.#", CultureInfo.InvariantCulture) + S_TipHours;
                    _tipMouseTime.Foreground = br;

                    if (rz.Level <= 20 && rz.Level > 0)
                    {
                        if (_settings.LowBatteryNotify && !_mouseLowNotified)
                        {
                            _mouseLowNotified = true;
                            _notifyIcon.ShowBalloonTip(5000, S_NotifyTitle, rz.FullName + " (" + rz.Level + "%)" + S_NotifyBodySuf, System.Windows.Forms.ToolTipIcon.Warning);
                            NativeEngine.Log("INFO", "[NOTIFY] Low battery alert for " + rz.FullName);
                        }
                    }
                    else if (rz.Level > 25)
                    {
                        _mouseLowNotified = false;
                    }
                }
            }
            else
            {
                _txtMouseLevel.Text = S_Offline;
                _txtMouseLevel.Foreground = gray;
                _pathMouseArc.Data = null;
                _txtMouseCharging.Visibility = Visibility.Collapsed;
                _tipMouseStatus.Text = S_TipOffline;
                _tipMouseTime.Text = "--";
                _tipMouseTime.Foreground = gray;
            }

            // 2. Universal Wireless Headset / Audio / Controller (SONY INZONE / Logicool G / Razer / Bluetooth)
            var sn = NativeEngine.GetUniversalHeadsetStatus();
            _txtHeadsetLabel.Text = sn.ShortName;
            _tipHeadsetTitle.Text = "\uD83C\uDFA7 " + sn.FullName;

            if (sn.Online && sn.NoLevel)
            {
                // Connected, but the device does not expose a battery % over USB: never show a fake value.
                _txtHeadsetLevel.Text = "ON";
                _txtHeadsetLevel.Foreground = gray;
                SetProgressArc(_pathHeadsetArc, 100, gray);
                _txtHeadsetCharging.Visibility = Visibility.Collapsed;
                _tipHeadsetStatus.Text = "Connected (battery % not available over USB)";
                _tipHeadsetTime.Text = "";
            }
            else if (sn.Online && sn.Level >= 0)
            {
                _txtHeadsetLevel.Text = sn.Level + "%";
                var br = GetLevelBrush(sn.Level);
                _txtHeadsetLevel.Foreground = br;
                SetProgressArc(_pathHeadsetArc, sn.Level, br);
                _txtHeadsetCharging.Visibility = sn.Charging ? Visibility.Visible : Visibility.Collapsed;

                if (sn.Charging)
                {
                    if (sn.Level >= 100)
                    {
                        _tipHeadsetStatus.Text = S_TipFullDone;
                        _tipHeadsetTime.Text = "";
                    }
                    else
                    {
                        int mins = Math.Max(5, (int)Math.Round((100 - sn.Level) * sn.ChargeMinsPerPct));
                        _tipHeadsetStatus.Text = S_TipCharging + " (" + sn.Level + "%)";
                        _tipHeadsetTime.Text = S_TipFullPref + mins + S_TipMins;
                    }
                    _tipHeadsetTime.Foreground = GetBrush("#FBBF24");
                    _headsetLowNotified = false;
                }
                else
                {
                    double hrs = Math.Max(0.2, Math.Round((sn.Level / 100.0) * sn.MaxHours, 1));
                    _tipHeadsetStatus.Text = S_TipDischarging + " (" + sn.Level + "%)";
                    _tipHeadsetTime.Text = S_TipRemainPref + hrs.ToString("0.#", CultureInfo.InvariantCulture) + S_TipHours;
                    _tipHeadsetTime.Foreground = br;

                    if (sn.Level <= 20 && sn.Level > 0)
                    {
                        if (_settings.LowBatteryNotify && !_headsetLowNotified)
                        {
                            _headsetLowNotified = true;
                            _notifyIcon.ShowBalloonTip(5000, S_NotifyTitle, sn.FullName + " (" + sn.Level + "%)" + S_NotifyBodySuf, System.Windows.Forms.ToolTipIcon.Warning);
                            NativeEngine.Log("INFO", "[NOTIFY] Low battery alert for " + sn.FullName);
                        }
                    }
                    else if (sn.Level > 25)
                    {
                        _headsetLowNotified = false;
                    }
                }
            }
            else
            {
                _txtHeadsetLevel.Text = S_Offline;
                _txtHeadsetLevel.Foreground = gray;
                _pathHeadsetArc.Data = null;
                _txtHeadsetCharging.Visibility = Visibility.Collapsed;
                _tipHeadsetStatus.Text = S_TipOffline;
                _tipHeadsetTime.Text = "--";
                _tipHeadsetTime.Foreground = gray;
            }
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_settings.AutoHideFullScreen && _hwnd != IntPtr.Zero)
            {
                string reason, winInfo;
                bool shouldHide = NativeEngine.EvaluateForeground(_hwnd, out reason, out winInfo);
                if (shouldHide != _lastHideState)
                {
                    _lastHideState = shouldHide;
                    if (shouldHide)
                    {
                        NativeEngine.Log("INFO", "[HIDE] " + reason + " | " + winInfo);
                        NativeEngine.ShowWindow(_hwnd, 0); // SW_HIDE
                    }
                    else
                    {
                        NativeEngine.Log("INFO", "[SHOW] " + reason + " | " + winInfo);
                        NativeEngine.ShowWindow(_hwnd, 8); // SW_SHOWNA
                    }
                }
            }

            _tickCount++;
            if (_tickCount % 7 == 0 && !_lastHideState)
            {
                UpdateBatteryDisplay();
            }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                if (enable)
                {
                    string exePath = Assembly.GetExecutingAssembly().Location;
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic sc = shell.CreateShortcut(_startupLnkPath);
                    sc.TargetPath = exePath;
                    sc.WorkingDirectory = _appDir;
                    sc.Description = "Ring Battery Monitor";
                    sc.Save();
                    NativeEngine.Log("INFO", "AutoStart enabled -> " + exePath);
                }
                else
                {
                    if (File.Exists(_startupLnkPath))
                    {
                        File.Delete(_startupLnkPath);
                        NativeEngine.Log("INFO", "AutoStart disabled");
                    }
                }
            }
            catch (Exception ex)
            {
                NativeEngine.Log("WARN", "AutoStart error: " + ex.Message);
            }
        }

        private void SaveCurrentSettings()
        {
            if (_window == null || _widgetScale == null) return;
            _settings.Left = _window.Left;
            _settings.Top = _window.Top;
            _settings.Scale = _widgetScale.ScaleX;
            _settings.Save(_configPath);
        }

        private void ShutdownApp()
        {
            SaveCurrentSettings();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            NativeEngine.Log("INFO", "Ring Battery Monitor Exited normally.");
            if (_window != null) _window.Close();
            Dispatcher.ExitAllFrames();
        }
    }
}
