using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
namespace affinityService
{
    internal class Program
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("kernel32.dll")] static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);
        static FileStream logger;
        static bool consoleOut = true;
        static Int32 interval = 10000;
        static Int32 selfAffinity = 0b1111_1111_0000_0000;
        static String proLassoCfgPartFile = "prolasso.ini";
        static String outFile = "config.ini";
        static String cfgFile = "affinityServiceConfig.ini";
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            string logDir = "logs";
            Directory.CreateDirectory(logDir);
            logger = new FileStream(Path.Combine(logDir, DateTime.Now.Date.ToString("yyyyMMdd") + ".log"), FileMode.Append, FileAccess.Write, FileShare.Read);
            bool convert = false;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (arg == "-help" || arg == "/?" || arg == "--help")
                {
                    Console.WriteLine("Usage: affinityService [options]");
                    Console.WriteLine("Options:");
                    Console.WriteLine("  -affinity <binary>    本程序的 CPU 亲和掩码 (二进制字符串，如 0b1111_0000)");
                    Console.WriteLine("  -console <true|false> 控制台输出日志");
                    Console.WriteLine("  -plfile <file>        ProcessLasso 配置的DefaultAffinitiesEx=此行之后的部分(默认 prolasso.ini)");
                    Console.WriteLine("                        示例（不换行）：steamwebhelper.exe,0,8-19,everything.exe,0,8-19");
                    Console.WriteLine("  -out <file>           转换输出 (默认 config.ini)");
                    Console.WriteLine("  -convert              从 ProcessLasso 文件的转换到本程序配置并退出");
                    Console.WriteLine("  -interval <ms>        遍历进程的停滞时间间隔 (毫秒, 默认 10000)");
                    Console.WriteLine("  -config <file>        指定配置(默认 affinityServiceConfig.ini)");
                    return;
                }
                else if (arg == "-affinity" && i + 1 < args.Length)
                    try { selfAffinity = Convert.ToInt32(args[++i].Replace("0b", ""), 2); }
                    catch { Console.WriteLine("无效的affinity"); }
                else if (arg == "-console" && i + 1 < args.Length && bool.TryParse(args[++i], out bool consoleFlag))
                    consoleOut = consoleFlag;
                else if (arg == "-plfile" && i + 1 < args.Length)
                    proLassoCfgPartFile = args[++i];
                else if (arg == "-outfile" && i + 1 < args.Length)
                    outFile = args[++i];
                else if (arg == "-convert")
                    convert = true;
                else if (arg == "-interval" && i + 1 < args.Length && int.TryParse(args[++i], out int interval))
                    Program.interval = interval;
                else if (arg == "-config" && i + 1 < args.Length)
                    cfgFile = args[++i];
            }
            if (convert)
            {
                ConvertCfg();
                return;
            }
            if (!consoleOut) ShowWindow(GetConsoleWindow(), 0);
            if (!AsAdmin()) Log("not as Admin");
            Log(SetProcessAffinityMask(Process.GetCurrentProcess().Handle, new IntPtr(selfAffinity)) ? "self affinity set " : "self affinity set failed");
            Dictionary<String, IntPtr> cfg = ReadConfig(cfgFile);
            object obj;
            int pid;
            Process prc;
            while (true)
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results.Cast<ManagementObject>())
                        try
                        {
                            obj = mo["ProcessId"];
                            if (obj == null) continue;
                            pid = Convert.ToInt32(obj);
                            prc = Process.GetProcessById(pid);
                            if (cfg.TryGetValue(Path.GetFileName(prc.MainModule.FileName).ToLower(), out IntPtr affinityMask)) if (!prc.ProcessorAffinity.Equals(affinityMask)) SetAffinity(prc, affinityMask);
                        }
                        catch { }
                }
                Thread.Sleep(interval);
            }
        }
        private static void Log(String s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s + "\r\n");
            logger.Write(bytes, 0, bytes.Length);
            logger.Flush();
            if (consoleOut) Console.WriteLine(s);
        }
        private static void SetAffinity(Process p, IntPtr a)
        {
            Log(p.Id + " " + p.ProcessName + "  -> " + a);
            SetProcessAffinityMask(p.Handle, a);
        }
        private static Dictionary<String, IntPtr> ReadConfig(string f)
        {
            FileStream fs = new FileStream(f, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            Dictionary<String, IntPtr> c = new Dictionary<String, IntPtr>();
            if (fs.CanRead)
            {
                byte[] bytes = new byte[fs.Length];
                while (fs.Read(bytes, 0, (int)fs.Length) != 0) ;
                foreach (var str0 in Encoding.UTF8.GetString(bytes).Split('\n'))
                    try
                    {
                        String str = str0.Replace("\r", "").ToLower();
                        if (str.Length == 0) continue;
                        if (str.StartsWith("#")) continue;
                        String[] s = str.Split(',');
                        if (s.Length != 2) continue;
                        Int32 affinityMask = Int32.Parse(s[1]);
                        c.Add(s[0], new IntPtr(affinityMask));
                    }
                    catch { }
            }
            fs.Close();
            return c;
        }
        static bool AsAdmin()
        {
            using (WindowsIdentity w = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(w).IsInRole(WindowsBuiltInRole.Administrator);
        }
        private static void ConvertCfg()
        {
            List<string[]> cfgs = CfgFromProLasso();
            FileStream configFile = new FileStream(outFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            String[] strs;
            for (int i = 0; i < cfgs.Count; i++)
            {
                strs = cfgs[i];
                byte[] bytes = Encoding.UTF8.GetBytes(strs[0] + "," + ParseAffinityRange(strs[2]) + "\r\n");
                configFile.Write(bytes, 0, bytes.Length);
            }
            configFile.Close();
        }
        private static int ParseAffinityRange(string r)
        {
            if (r == null) return 0;
            int m = 0;
            foreach (string p in r.Split(','))
                if (p.Contains('-'))
                {
                    string[] s = p.Split('-');
                    if (int.TryParse(s[0], out int start) && int.TryParse(s[1], out int end)) for (int i = start; i <= end; i++) m |= 1 << i;
                }
                else if (int.TryParse(p, out int core)) m |= 1 << core;
            return m;
        }
        private static List<string[]> CfgFromProLasso()
        {
            FileStream fs = new FileStream(proLassoCfgPartFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            List<string[]> configList = new List<string[]>();
            if (fs.CanRead)
            {
                StringBuilder sb = new StringBuilder();

                byte[] bytes = new byte[fs.Length];
                fs.Read(bytes, 0, (int)fs.Length);
                char[] chars = Encoding.UTF8.GetString(bytes).ToCharArray();
                Int32 b = 0;
                String[] strings = new String[3];
                for (global::System.Int32 i = 0; i < chars.Length; i++)
                {
                    char c = chars[i];
                    if (c == ',')
                    {
                        strings[b++] = sb.ToString();
                        sb.Clear();
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    if (b == 3)
                    {
                        configList.Add(strings);
                        strings = new String[3];
                        b = 0;
                    }
                }
                fs.Close();
            }
            else fs.Close();
            return configList;
        }
    }
}
