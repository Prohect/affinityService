using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;


namespace affinityService
{
    internal class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            Process p = Process.GetCurrentProcess();
            Int32 affinity_e = 0b11111111111100000000;
            IntPtr mask = new IntPtr(affinity_e);
            Console.WriteLine(SetProcessAffinityMask(p.Handle, mask) ? "自身进程亲和性修改成功" : "自身进程亲和性修改失败");

            Dictionary<String, IntPtr> config = ReadConfigListFromConfigFile("config.txt");

            string query = "SELECT ProcessId FROM Win32_Process";
            while (true)
            {
                using (var searcher = new ManagementObjectSearcher(query))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results.Cast<ManagementObject>())
                    {
                        try
                        {
                            var pidObj = mo["ProcessId"];
                            if (pidObj == null) continue;
                            int pid = Convert.ToInt32(pidObj);
                            Process proc = null;
                            try
                            {
                                proc = Process.GetProcessById(pid);
                            }
                            catch (ArgumentException aex)
                            {
                                Console.WriteLine("  -> 进程已退出 (GetProcessById 抛出 ArgumentException)" + aex.Message);
                                continue;
                            }

                            /*// 打印一些 Process 可读属性（某些访问可能抛异常）
                            try
                            {
                                Console.WriteLine($"  Process.ProcessName: {proc.ProcessName}");
                                Console.WriteLine($"  Id: {proc.Id}");
                                Console.WriteLine($"  SessionId: {proc.SessionId}");
                                Console.WriteLine($"  StartTime: {SafeGet(() => proc.StartTime)}");
                                Console.WriteLine($"  Threads: {SafeGet(() => proc.Threads.Count)}");
                            }
                            catch (Win32Exception wex)
                            {
                                Console.WriteLine("  -> 访问 Process 属性被拒绝: " + wex.Message);
                            }
                            catch (InvalidOperationException iex)
                            {
                                Console.WriteLine("  -> " + iex.Message);
                            }*/

                            try
                            {
                                if (config.TryGetValue(proc.ProcessName, out IntPtr affinityMask)) if (!proc.ProcessorAffinity.Equals(affinityMask)) SetProcessAffinityMask(proc.Handle, affinityMask);
                            }
                            catch (Win32Exception wex)
                            {
                                Console.WriteLine("  -> 读取/设置 ProcessorAffinity 被拒绝: " + wex.Message);
                            }
                            catch (InvalidOperationException iex)
                            {
                                Console.WriteLine("  -> 进程已退出，无法读取/设置亲和性" + iex.Message);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("遍历时发生异常: " + ex.Message);
                        }
                    }
                }
            }
        }

        private static List<string[]> ConvertFromProcessLassoConfigPartFileNameAndGetConfigList(String processLassoConfigPartFileName, String outputFileName)
        {
            List<string[]> configList = ReadConfigListFromProcessLassoConfigPartFileName(processLassoConfigPartFileName);

            FileStream configFile = new FileStream(outputFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            String[] strings1;
            for (int i = 0; i < configList.Count; i++)
            {
                strings1 = configList[i];
                String line = strings1[0] + "," + strings1[2] + "\r\n";
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                configFile.Write(bytes, 0, bytes.Length);
            }
            configFile.Close();
            return configList;
        }

        private static List<string[]> ReadConfigListFromProcessLassoConfigPartFileName(string processLassoConfigPartFileName)
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

        private static Dictionary<String, IntPtr> ReadConfigListFromConfigFile(string configFile)
        {
            FileStream fs = new FileStream(configFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Dictionary<String, IntPtr> config = new Dictionary<String, IntPtr>();
            if (fs.CanRead)
            {
                StringBuilder sb = new StringBuilder();

                byte[] bytes = new byte[fs.Length];
                fs.Read(bytes, 0, (int)fs.Length);
                String all = Encoding.UTF8.GetString(bytes);
                String[] strs = all.Split('\n');

                for (global::System.Int32 i = 0; i < strs.Length; i++)
                {
                    try
                    {
                        String str = strs[i];
                        str = str.Trim();
                        if (str.Length == 0) continue;
                        String[] parts = str.Split(',');
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

        static string SafeGet(Func<object> getter)
        {
            try
            {
                var v = getter();
                return v?.ToString() ?? "<null>";
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                return $"<error: {ex.Message}>";
            }
        }
    }
}
