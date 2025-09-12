using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        //[DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        static FileStream logger;
        static bool consoleOutput = true;
        static Int32 timeInterval = 10000;
        static Int32 selfAffinity = 0b0000_0000_0000_0000_1111_1111_0000_0000;
        static String processLassoConfigPartFileName = "prolasso.ini";
        static String outputFileName = "config.ini";
        static String configFileName = "processAffinityServiceConfig.ini";
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            string logDir = "logs";
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, DateTime.Now.Date.ToString("yyyyMMdd") + ".log");
            logger = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);

            //read args
            bool convertOnly = false;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (arg == "-help" || arg == "/?" || arg == "--help")
                {
                    Console.WriteLine("Usage: affinityService [options]");
                    Console.WriteLine("Options:");
                    Console.WriteLine("  -affinity <binary>         设置本程序的 CPU 亲和掩码 (二进制字符串，如 0b1111_0000)");
                    Console.WriteLine("  -console <true|false>      是否在控制台输出日志");
                    Console.WriteLine("  -plfile <file>             ProcessLasso 配置的DefaultAffinitiesEx=此行之后的部分的文件(默认 prolasso.ini)");
                    Console.WriteLine("                             内容示例（不换行）：steamwebhelper.exe,0,8-19,everything.exe,0,8-19");
                    Console.WriteLine("  -outfile <file>            输出转换的配置文件名 (默认 config.ini)");
                    Console.WriteLine("  -convert                   从 ProcessLasso 文件的转换到本程序配置并退出");
                    Console.WriteLine("  -interval <ms>             遍历进程的停滞时间间隔 (毫秒, 默认 10000)");
                    Console.WriteLine("  -config <file>             指定配置文件(默认 processAffinityServiceConfig.ini)");
                    return;
                }
                else if (arg == "-affinity" && i + 1 < args.Length)
                    try { selfAffinity = Convert.ToInt32(args[++i].Replace("0b", ""), 2); }
                    catch { Console.WriteLine("无效的 affinity 参数，保持默认"); }
                else if (arg == "-console" && i + 1 < args.Length && bool.TryParse(args[++i], out bool consoleFlag))
                    consoleOutput = consoleFlag;
                else if (arg == "-plfile" && i + 1 < args.Length)
                    processLassoConfigPartFileName = args[++i];
                else if (arg == "-outfile" && i + 1 < args.Length)
                    outputFileName = args[++i];
                else if (arg == "-convert")
                    convertOnly = true;
                else if (arg == "-interval" && i + 1 < args.Length && int.TryParse(args[++i], out int interval))
                    timeInterval = interval;
                else if (arg == "-config" && i + 1 < args.Length)
                    configFileName = args[++i];
            }
            if (convertOnly)
            {
                ConvertFromProcessLassoConfigPartFileNameAndGetConfigList();
                return;
            }
            if (!consoleOutput) ShowWindow(GetConsoleWindow(), 0);
            //done read args

            if (!IsRunningAsAdmin()) Log("running not as Administrator, may not able to set affinity for some process");
            Log(SetProcessAffinityMask(Process.GetCurrentProcess().Handle, new IntPtr(selfAffinity)) ? "self affinity set success " : "self affinity set failed");

            Dictionary<String, IntPtr> config = ReadConfigListFromConfigFile(configFileName);
            string query = "SELECT ProcessId FROM Win32_Process";
            object pidObj;
            int pid = -1;
            Process process = null;
            while (true)
            {
                using (var searcher = new ManagementObjectSearcher(query))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results.Cast<ManagementObject>())
                    {
                        try
                        {
                            pidObj = mo["ProcessId"];
                            if (pidObj == null) continue;
                            pid = Convert.ToInt32(pidObj);
                            process = Process.GetProcessById(pid);
                            if (config.TryGetValue(Path.GetFileName(process.MainModule.FileName).ToLower(), out IntPtr affinityMask)) if (!process.ProcessorAffinity.Equals(affinityMask)) InnerSetProcessAffinityMask(process, affinityMask);
                            pid = -1;
                            process = null;
                        }
                        catch (ArgumentException e)
                        {
                            //Log((process == null ? pid == -1 ? "pid is null!" : pid.ToString() : process.ProcessName) + "  -> 进程已退出 (GetProcessById 抛出 ArgumentException)" + e.Message);
                        }
                        catch (Win32Exception e)
                        {
                            //Log((process == null ? pid == -1 ? "pid is null!" : pid.ToString() : process.ProcessName) + "  -> 读取/设置 ProcessorAffinity 被拒绝: " + e.Message);
                        }
                        catch (InvalidOperationException e)
                        {
                            //Log((process == null ? pid == -1 ? "pid is null!" : pid.ToString() : process.ProcessName) + "  -> 进程已退出，无法读取/设置亲和性" + e.Message);
                        }
                        catch (Exception e)
                        {
                            Log((process == null ? pid == -1 ? "pid is null!" : pid.ToString() : process.ProcessName) + "遍历时发生异常: " + e.Message);
                        }
                    }
                }
                Thread.Sleep(timeInterval);
            }
        }

        private static void Log(String log)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(log + "\r\n");
            logger.Write(bytes, 0, bytes.Length);
            logger.Flush();
            if (consoleOutput) Console.WriteLine(log);
        }

        private static void InnerSetProcessAffinityMask(Process process, IntPtr dwProcessAffinityMask)
        {
            Log("setAffinity:" + process.ProcessName + "  ->\t" + dwProcessAffinityMask);
            SetProcessAffinityMask(process.Handle, dwProcessAffinityMask);
        }
        private static Dictionary<String, IntPtr> ReadConfigListFromConfigFile(string configFile)
        {
            FileStream fs = new FileStream(configFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            Dictionary<String, IntPtr> config = new Dictionary<String, IntPtr>();
            if (fs.CanRead)
            {
                byte[] bytes = new byte[fs.Length];
                while (fs.Read(bytes, 0, (int)fs.Length) != 0) ;
                foreach (var str0 in Encoding.UTF8.GetString(bytes).Split('\n'))
                {
                    try
                    {
                        String str = str0.Replace("\r", "").ToLower();
                        if (str.Length == 0) continue;
                        if (str.StartsWith("#")) continue;
                        String[] parts = str.Split(',');
                        if (parts.Length != 2) continue;
                        Int32 affinityMask = Int32.Parse(parts[1]);
                        config.Add(parts[0], new IntPtr(affinityMask));
                    }
                    catch { }
                }
                fs.Close();
            }
            else fs.Close();
            return config;
        }

        static bool IsRunningAsAdmin()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        private static void ConvertFromProcessLassoConfigPartFileNameAndGetConfigList()
        {
            List<string[]> configList = ReadConfigListFromProcessLassoConfigPartFileName();

            FileStream configFile = new FileStream(outputFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            String[] strings1;
            byte[] head = Encoding.UTF8.GetBytes("#\tthe affinity mask is a int32 value, windows us its binary form to set affinity for process\r\n#\teg.\t254=0b0000_0000_1111_1110 refers cores(1-7),which don't not include core0 and all other cores");
            configFile.Write(head, 0, head.Length);
            for (int i = 0; i < configList.Count; i++)
            {
                strings1 = configList[i];
                String line = strings1[0] + "," + ParseAffinityRange(strings1[2]) + "\r\n";
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                configFile.Write(bytes, 0, bytes.Length);
            }
            configFile.Close();
        }

        private static int ParseAffinityRange(string range)
        {
            range = range.Trim();
            if (string.IsNullOrEmpty(range)) return 0;
            int mask = 0;
            string[] parts = range.Split(',');
            foreach (string part in parts)
            {
                if (part.Contains('-'))
                {
                    string[] bounds = part.Split('-');
                    if (bounds.Length != 2) continue;

                    if (int.TryParse(bounds[0], out int start) && int.TryParse(bounds[1], out int end))
                    {
                        for (int i = start; i <= end; i++)
                        {
                            mask |= 1 << i;
                        }
                    }
                }
                else
                {
                    if (int.TryParse(part, out int core))
                    {
                        mask |= 1 << core;
                    }
                }
            }
            return mask;
        }



        private static List<string[]> ReadConfigListFromProcessLassoConfigPartFileName()
        {
            FileStream fs = new FileStream(processLassoConfigPartFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
