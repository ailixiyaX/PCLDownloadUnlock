using System;
using System.IO;
using System.Text;
using PCLDownloadUnlock.Core;

namespace PCLDownloadUnlock
{
    /// <summary>命令行模式：保留给脚本 / 批量部署使用。</summary>
    internal static class Cli
    {
        private sealed class ConsoleLog : ILog
        {
            public void Info(string message) => Console.WriteLine(message);
            public void Warn(string message) => Console.WriteLine("[注意] " + message);
            public void Error(string message) => Console.Error.WriteLine("[错误] " + message);
        }

        public static bool LooksLikeCli(string[] args)
        {
            if (args.Length == 0) return false;
            foreach (var a in args)
            {
                if (a.StartsWith("-", StringComparison.Ordinal)) return true;
                if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static int Run(string[] args)
        {
            ConsoleBridge.Attach();

            try
            {
                var revert = false;
                var checkOnly = false;
                string target = null;

                foreach (var a in args)
                {
                    if (a is "--revert" or "-r") revert = true;
                    else if (a is "--check" or "-c") checkOnly = true;
                    else if (a is "--help" or "-h" or "/?") { PrintUsage(); return 0; }
                    else if (target == null) target = a;
                    else
                    {
                        Console.Error.WriteLine("[错误] 多余的参数：" + a);
                        return 2;
                    }
                }

                try
                {
                    if (!Console.IsOutputRedirected) Console.OutputEncoding = Encoding.UTF8;
                }
                catch
                {
                    // 没有真正的控制台时（输出被重定向 / 无控制台）无法设置代码页
                }

                if (target == null)
                {
                    var guess = Path.Combine(Environment.CurrentDirectory, "PCL2_CE_x64.exe");
                    if (File.Exists(guess)) target = guess;
                }

                if (target == null || !File.Exists(target))
                {
                    PrintUsage();
                    return 2;
                }

                target = Path.GetFullPath(target);
                var log = new ConsoleLog();

                if (revert)
                {
                    Console.WriteLine("目标文件：" + target);
                    return PatchEngine.Revert(target, log) ? 0 : 1;
                }

                var analysis = PatchEngine.Analyze(target);
                Console.WriteLine("目标文件：" + target);
                Console.WriteLine("文件版本：" + analysis.Version + "   大小：" + analysis.Size + " 字节");
                if (analysis.Sha256 != null) Console.WriteLine("SHA-256 ：" + analysis.Sha256);
                if (analysis.IsBundle) Console.WriteLine("单文件包：" + analysis.BundleInfo);
                Console.WriteLine();

                foreach (var s in analysis.Sites)
                {
                    Console.WriteLine("  " + s.Name.PadRight(20) + " RVA=0x" + s.Rva.ToString("X") +
                                      "  IL=" + s.CodeSize + " 字节  文件偏移=" + s.FileOffset +
                                      (s.PatchApplied ? "  [已打补丁]" : "  [未打补丁]"));
                }

                foreach (var w in analysis.Warnings) Console.WriteLine("[注意] " + w);
                Console.WriteLine();

                if (!analysis.Compatible)
                {
                    Console.Error.WriteLine("[失败] " + analysis.Failure);
                    return analysis.Status == PatchStatus.NotBundle ? 3 : 4;
                }

                if (checkOnly)
                {
                    Console.WriteLine("状态：" + analysis.StatusText);
                    Console.WriteLine("[检查结束] 未修改任何文件。");
                    return analysis.AllPatched ? 0 : 1;
                }

                if (analysis.AllPatched)
                {
                    Console.WriteLine("状态：已打补丁，无需修改。");
                    return 0;
                }

                PatchEngine.Apply(target, log);
                Console.WriteLine();
                Console.WriteLine("[完成] 现在启动 PCL，中国大陆地区限制应该已解除。");
                return 0;
            }
            catch (PatchException ex)
            {
                Console.Error.WriteLine("[失败] " + ex.Message);
                return ex.Code switch
                {
                    "busy" => 6,
                    "not-bundle" => 3,
                    "incompatible" => 4,
                    "verify" => 5,
                    _ => 1
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[失败] " + ex.GetType().Name + "：" + ex.Message);
                if (Environment.GetEnvironmentVariable("PCLFIX_DEBUG") == "1") Console.Error.WriteLine(ex.ToString());
                return 1;
            }
            finally
            {
                if (Environment.UserInteractive && !Console.IsInputRedirected)
                {
                    Console.WriteLine();
                    Console.Write("按回车键退出...");
                    try { Console.ReadLine(); } catch { }
                }
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("PCL 下载解锁 (PCLDownloadUnlock) " + AppInfo.Version);
            Console.WriteLine();
            Console.WriteLine("用法：");
            Console.WriteLine("  PCLDownloadUnlock.exe                       打开图形界面");
            Console.WriteLine("  PCLDownloadUnlock.exe <PCL.exe> [--check] [--revert]");
            Console.WriteLine();
            Console.WriteLine("  --check     只检查状态，不修改任何文件（已打补丁返回 0，未打补丁返回 1）");
            Console.WriteLine("  --revert    从 *.pclfix.bak 备份还原原始文件");
            Console.WriteLine();
            Console.WriteLine("说明：补丁只改 .NET 程序集里两个方法的 IL 代码，等长就地改写，");
            Console.WriteLine("      不改变文件大小、不破坏单文件包结构。原文件会备份为 *.pclfix.bak。");
        }
    }
}
