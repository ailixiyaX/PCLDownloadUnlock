using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace PCLDownloadUnlock.Core
{
    public interface ILog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    public enum PatchStatus
    {
        Unknown,
        NotBundle,      // 不是 .NET 单文件包
        Incompatible,   // 单文件包正常，但没有找到可补丁的限制逻辑
        NotPatched,
        Patched
    }

    public sealed class PatchSiteInfo
    {
        public string Name;
        public string Effect;          // 人类可读的效果说明
        public int Rva;
        public int CodeSize;
        public long FileOffset;        // 在 exe 里的绝对偏移
        public bool PatchApplied;
    }

    public sealed class Analysis
    {
        public string Path;
        public string FileName;
        public string Version = "未知";
        public long Size;
        public string Sha256;
        public bool IsBundle;
        public string BundleInfo;
        public PatchStatus Status = PatchStatus.Unknown;
        public readonly List<PatchSiteInfo> Sites = new List<PatchSiteInfo>();
        public readonly List<string> Warnings = new List<string>();
        public string Failure;            // 非空表示不能打补丁的原因
        public bool HasBackup;
        public bool IsLocked;

        public bool AllPatched => Sites.Count > 0 && Sites.All(s => s.PatchApplied);
        public bool Compatible => IsBundle && Sites.Count > 0;
        public bool NonePatched => Sites.Count > 0 && Sites.All(s => !s.PatchApplied);

        public string StatusText => Status switch
        {
            PatchStatus.Patched => "已打补丁",
            PatchStatus.NotPatched => "未打补丁",
            PatchStatus.Incompatible => "不适用（未找到限制逻辑）",
            PatchStatus.NotBundle => "不是 .NET 单文件包",
            _ => "未知"
        };
    }

    public sealed class ApplyReport
    {
        public bool Changed;
        public readonly List<string> Patched = new List<string>();
        public readonly List<string> Already = new List<string>();
        public string BackupPath;
        public Analysis After;
    }

    public sealed class PatchException : Exception
    {
        public string Code { get; }

        public PatchException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// 补丁核心。只做两件事：把 PCL.RestrictedFeaturePolicy.Check 改成恒返回 true，
    /// 把 PCL.RestrictedFeaturePolicy.get_IsRestricted 改成恒返回 false。
    /// 等长就地改写 IL 并把方法体头的 codeSize 缩短为 2，不触碰元数据、不改变文件大小与单文件包结构。
    /// </summary>
    public static class PatchEngine
    {
        public const string TargetNamespace = "PCL";
        public const string TargetType = "RestrictedFeaturePolicy";
        private const string CheckMethod = "Check";
        private const string RestrictedProperty = "get_IsRestricted";

        // Check(...) 原始逻辑：未受限时 ldc.i4.1; ret，受限时 ldc.i4.0; ret。
        private static readonly byte[] CheckIl = { 0x17, 0x2A };        // ldc.i4.1; ret  → 始终"允许"
        private static readonly byte[] RestrictedIl = { 0x16, 0x2A };   // ldc.i4.0; ret  → 始终"未受限"

        public static string BackupPath(string target) => target + ".pclfix.bak";

        private sealed class Site
        {
            public string Name;
            public string Effect;
            public int Rva;
            public int BodyOffset;     // 方法体头在 dll 内的偏移
            public int IlOffset;       // IL 在 dll 内的偏移
            public int CodeSize;
            public bool IsFat;
            public byte[] Expected;
            public long FileOffset;    // IL 在 exe 内的偏移
        }

        // ---------------- 分析 ----------------

        public static Analysis Analyze(string path, bool computeHash = true)
        {
            var result = new Analysis
            {
                Path = path,
                FileName = System.IO.Path.GetFileName(path)
            };

            var info = new FileInfo(path);
            result.Size = info.Length;
            result.HasBackup = File.Exists(BackupPath(path));
            result.IsLocked = IsLocked(path);

            try
            {
                result.Version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? "未知";
            }
            catch
            {
                // 忽略
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                result.Failure = "读取文件失败：" + ex.Message;
                result.Status = PatchStatus.Unknown;
                return result;
            }

            if (computeHash)
            {
                result.Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            }

            if (!BundleImage.TryParse(bytes, out var bundle, out var why))
            {
                result.IsBundle = false;
                result.Status = PatchStatus.NotBundle;
                result.Failure = why;
                return result;
            }

            result.IsBundle = true;
            result.BundleInfo = bundle.Describe();

            foreach (var e in bundle.Entries.Where(x => x.Type == 1))
            {
                if (e.CompressedSize != 0)
                {
                    result.Warnings.Add(e.Path + "：压缩条目，无法就地补丁");
                    continue;
                }

                var dll = new byte[e.Size];
                Array.Copy(bytes, e.Offset, dll, 0, e.Size);

                if (!TryLocate(dll, out var sites)) continue;

                foreach (var s in sites)
                {
                    s.FileOffset = e.Offset + s.IlOffset;
                    result.Sites.Add(new PatchSiteInfo
                    {
                        Name = s.Name,
                        Effect = s.Effect,
                        Rva = s.Rva,
                        CodeSize = s.CodeSize,
                        FileOffset = s.FileOffset,
                        PatchApplied = IsPatched(bytes, (int)s.FileOffset, (int)(e.Offset + s.BodyOffset), s)
                    });
                }
            }

            if (result.Sites.Count == 0)
            {
                result.Status = PatchStatus.Incompatible;
                result.Failure = "没有在单文件包里找到 " + TargetNamespace + "." + TargetType +
                                 "（PCL 版本可能不包含区域限制，或该版本已改动实现）";
                return result;
            }

            result.Status = result.AllPatched ? PatchStatus.Patched : PatchStatus.NotPatched;
            return result;
        }

        // ---------------- 打补丁 ----------------

        public static ApplyReport Apply(string path, ILog log)
        {
            if (IsLocked(path))
            {
                throw new PatchException("busy", "无法写入目标文件：PCL 正在运行（或被其他程序占用）。请先完全关闭 PCL（含托盘图标）后重试。");
            }

            var bytes = File.ReadAllBytes(path);
            if (!BundleImage.TryParse(bytes, out var bundle, out var why))
            {
                throw new PatchException("not-bundle", why);
            }

            var report = new ApplyReport();

            foreach (var e in bundle.Entries.Where(x => x.Type == 1))
            {
                if (e.CompressedSize != 0) continue;

                var dll = new byte[e.Size];
                Array.Copy(bytes, e.Offset, dll, 0, e.Size);
                if (!TryLocate(dll, out var sites)) continue;

                log?.Info("程序集：" + e.Path + "（bundle 偏移 " + e.Offset + "，大小 " + e.Size + "）");

                foreach (var s in sites)
                {
                    var ilPos = (int)(e.Offset + s.IlOffset);
                    var bodyPos = (int)(e.Offset + s.BodyOffset);
                    log?.Info("  " + s.Name.PadRight(20) + " RVA=0x" + s.Rva.ToString("X") +
                              "  IL=" + s.CodeSize + " 字节  文件偏移=" + ilPos);

                    if (IsPatched(bytes, ilPos, bodyPos, s))
                    {
                        report.Already.Add(s.Name);
                        log?.Info("    → 已经打过补丁，跳过");
                        continue;
                    }

                    // 关键点：写 2 字节 IL，并把方法体头里的 codeSize 改成 2。
                    // 不能把尾部填 nop —— ret 之后如果还有能贯穿到 IL 末尾的代码，
                    // CoreCLR 会直接把整个方法判为 InvalidProgramException。
                    bytes[ilPos] = s.Expected[0];
                    bytes[ilPos + 1] = s.Expected[1];
                    if (s.IsFat) BitConverter.TryWriteBytes(new Span<byte>(bytes, bodyPos + 4, 4), 2);
                    else bytes[bodyPos] = (byte)((2 << 2) | 0x02);

                    report.Patched.Add(s.Name);
                    log?.Info("    → 已写入补丁（" + s.Effect + "）");
                }
            }

            if (report.Patched.Count == 0 && report.Already.Count == 0)
            {
                throw new PatchException("incompatible",
                    "没有在单文件包里找到 " + TargetNamespace + "." + TargetType + "，该版本不适用本补丁。");
            }

            if (report.Patched.Count == 0)
            {
                log?.Info("补丁已存在，文件无需修改。");
                report.After = Analyze(path);
                return report;
            }

            var bak = BackupPath(path);
            if (Environment.GetEnvironmentVariable("PCLFIX_DEBUG") == "1")
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                try
                {
                    var probe = System.IO.Path.Combine(dir, "pclfix_probe.txt");
                    File.WriteAllText(probe, "probe");
                    log?.Info("[dbg] probe write ok: " + probe);
                }
                catch (Exception ex)
                {
                    log?.Info("[dbg] probe write FAIL: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X") + " " + ex.Message);
                }
            }
            if (!File.Exists(bak))
            {
                try
                {
                    File.Copy(path, bak);
                }
                catch (Exception ex)
                {
                    log?.Info("[dbg] copy FAIL: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X") + " " + ex.Message);
                    throw;
                }
                report.BackupPath = bak;
                log?.Info("已备份原始文件：" + bak);
            }
            else
            {
                report.BackupPath = bak;
                log?.Info("备份文件已存在，保持不变：" + bak);
            }

            WriteFileSafely(path, bytes);
            log?.Info("已写入：" + string.Join("、", report.Patched));

            // 复核
            var verify = File.ReadAllBytes(path);
            if (!BundleImage.TryParse(verify, out var b2, out var why2))
            {
                Restore(path);
                throw new PatchException("verify", "补丁后无法重新解析单文件包：" + why2 + "（已自动回滚）");
            }

            foreach (var e in b2.Entries.Where(x => x.Type == 1))
            {
                var dll = new byte[e.Size];
                Array.Copy(verify, e.Offset, dll, 0, e.Size);
                if (!TryLocate(dll, out var sites)) continue;

                foreach (var s in sites)
                {
                    if (!IsPatched(verify, (int)(e.Offset + s.IlOffset), (int)(e.Offset + s.BodyOffset), s))
                    {
                        Restore(path);
                        throw new PatchException("verify", "补丁复核不通过：" + s.Name + "（已自动回滚）");
                    }
                }
            }

            log?.Info("补丁复核通过。");
            report.Changed = true;
            report.After = Analyze(path);
            return report;
        }

        // ---------------- 还原 ----------------

        public static bool Revert(string path, ILog log)
        {
            var bak = BackupPath(path);
            if (!File.Exists(bak))
            {
                log?.Warn("没有找到备份文件：" + bak);
                return false;
            }

            if (IsLocked(path))
            {
                throw new PatchException("busy", "无法写入目标文件：PCL 正在运行。请先完全关闭 PCL 后重试。");
            }

            var bakBytes = File.ReadAllBytes(bak);
            WriteFileSafely(path, bakBytes);

            // 还原后校验
            if (!BundleImage.TryParse(File.ReadAllBytes(path), out _, out var why))
            {
                throw new PatchException("verify", "还原后的文件不是有效的单文件包：" + why);
            }

            log?.Info("已从备份还原：" + bak);
            return true;
        }

        // ---------------- 辅助 ----------------

        public static bool IsLocked(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 只读属性或被 ACL 限制，交给调用方按写入失败处理
                return false;
            }
        }

        private static void WriteFileSafely(string path, byte[] bytes)
        {
            var attrs = File.GetAttributes(path);
            var readOnly = (attrs & FileAttributes.ReadOnly) != 0;
            if (readOnly) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

            var tmp = path + ".pclfix.tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                try
                {
                    File.Replace(tmp, path, null);
                }
                catch (Exception)
                {
                    // 某些卷（或权限）不支持 Replace，退化为覆盖写
                    File.Copy(tmp, path, true);
                }
            }
            finally
            {
                TryDelete(tmp);
                if (readOnly)
                {
                    try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); } catch { }
                }
            }
        }

        private static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        private static void Restore(string path)
        {
            var bak = BackupPath(path);
            if (!File.Exists(bak)) return;
            try
            {
                File.Copy(bak, path, true);
            }
            catch
            {
                // 回滚失败时保留现场，由调用方提示用户手工还原
            }
        }

        private static bool IsPatched(byte[] data, int ilPos, int bodyPos, Site s)
        {
            if (ilPos + 1 >= data.Length || bodyPos >= data.Length) return false;
            if (data[ilPos] != s.Expected[0] || data[ilPos + 1] != s.Expected[1]) return false;

            int codeSize;
            if (s.IsFat)
            {
                if (bodyPos + 8 > data.Length) return false;
                codeSize = (int)BitConverter.ToUInt32(data, bodyPos + 4);
            }
            else
            {
                codeSize = data[bodyPos] >> 2;
            }

            return codeSize == 2;
        }

        private static bool TryLocate(byte[] dll, out List<Site> sites)
        {
            sites = null;
            try
            {
                using var pe = PeImage.Open(dll);
                if (!pe.HasMetadata) return false;

                var md = pe.GetMetadataReader();
                var handle = PeImage.FindType(md, TargetNamespace, TargetType);
                if (handle == null) return false;

                var td = md.GetTypeDefinition(handle.Value);
                var list = new List<Site>();

                foreach (var mh in td.GetMethods())
                {
                    var mdef = md.GetMethodDefinition(mh);
                    var name = md.GetString(mdef.Name);

                    byte[] expected;
                    string effect;
                    if (name == CheckMethod)
                    {
                        expected = CheckIl;
                        effect = "任何下载/安装操作都被视为允许";
                    }
                    else if (name == RestrictedProperty)
                    {
                        expected = RestrictedIl;
                        effect = "区域判定恒为「未受限」";
                    }
                    else
                    {
                        continue;
                    }

                    var rva = mdef.RelativeVirtualAddress;
                    if (rva <= 0) continue;

                    var bodyOff = PeImage.RvaToOffset(pe, rva);
                    if (bodyOff < 0) continue;

                    if (!PeImage.TryReadBody(dll, bodyOff, out var ilOff, out var codeSize, out var isFat)) continue;
                    if (codeSize < expected.Length) continue;

                    list.Add(new Site
                    {
                        Name = name,
                        Effect = effect,
                        Rva = rva,
                        BodyOffset = bodyOff,
                        IlOffset = ilOff,
                        CodeSize = codeSize,
                        IsFat = isFat,
                        Expected = expected
                    });
                }

                if (list.Count == 0) return false;
                sites = list;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
