using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PCLDownloadUnlock
{
    /// <summary>
    /// WPF 程序属于窗口子系统，从控制台启动时没有 stdout。
    /// 这里把进程附加到父控制台，让命令行模式也能正常输出；
    /// 如果标准输出已经被重定向（管道 / 文件），则保持原样。
    /// </summary>
    internal static class ConsoleBridge
    {
        private const int AttachParentProcess = -1;
        private const int StdOutputHandle = -11;
        private const int StdErrorHandle = -12;
        private const int StdInputHandle = -10;
        private const int FileTypeChar = 2;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int GetFileType(IntPtr hFile);

        private static bool IsValid(IntPtr h) => h != IntPtr.Zero && h != new IntPtr(-1);

        public static void Attach()
        {
            try
            {
                var stdout = GetStdHandle(StdOutputHandle);
                var redirected = IsValid(stdout) && GetFileType(stdout) != FileTypeChar;

                if (!redirected)
                {
                    if (!AttachConsole(AttachParentProcess)) AllocConsole();

                    try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); } catch { }
                    try { Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true }); } catch { }
                    try { Console.SetIn(new StreamReader(Console.OpenStandardInput())); } catch { }
                }
                else
                {
                    try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); } catch { }
                    try { Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true }); } catch { }
                }

                Console.WriteLine();
            }
            catch
            {
                // 拿不到控制台就静默继续，不影响命令本身
            }
        }
    }

    internal static class AppInfo
    {
        public const string Version = "v1.1.0";
        public const string RepoUrl = "https://github.com/ailixiyaX/PCLDownloadUnlock";
        public const string PrUrl = "https://github.com/PCL-Community/PCL-CE/pull/3673";
    }
}
