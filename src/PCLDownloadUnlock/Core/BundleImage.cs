using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace PCLDownloadUnlock.Core
{
    /// <summary>.NET 单文件包（bundle）里的一个条目。</summary>
    public sealed class BundleEntry
    {
        public long Offset;
        public long Size;
        public long CompressedSize;
        public byte Type;       // 1 = 程序集
        public string Path;
    }

    /// <summary>.NET 单文件包（bundle）的头部信息。</summary>
    public sealed class BundleImage
    {
        public long HeaderOffset;
        public uint Major;
        public uint Minor;
        public int FileCount;
        public string BundleId;
        public readonly List<BundleEntry> Entries = new List<BundleEntry>();

        public string Describe() =>
            "v" + Major + "." + Minor + "，" + FileCount + " 个条目，头部偏移 " + HeaderOffset +
            " (0x" + HeaderOffset.ToString("X") + ")，id=" + BundleId;

        // .NET 单文件包的 32 字节 bundle marker（apphost 里紧挨着 header 偏移字段）
        private static readonly byte[] Marker =
        {
            0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
            0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
        };

        /// <summary>尝试把整个文件识别为 .NET 单文件包。</summary>
        public static bool TryParse(byte[] file, out BundleImage bundle, out string why)
        {
            bundle = null;
            why = null;

            var candidates = new List<long>();
            var markerPos = IndexOf(file, Marker);
            if (markerPos >= 8) candidates.Add(BitConverter.ToInt64(file, (int)(markerPos - 8)));
            if (markerPos >= 16) candidates.Add(BitConverter.ToInt64(file, (int)(markerPos - 16)));

            foreach (var c in candidates)
            {
                if (TryParseAt(file, c, out bundle)) return true;
            }

            // 兜底：8 字节对齐扫描（应对 marker 被改写或旧版布局）
            for (long off = 0; off + 48 < file.Length; off += 8)
            {
                if (TryParseAt(file, off, out bundle)) return true;
            }

            why = "找不到符合结构的单文件包头部（这不是 .NET 单文件发布的 PCL？）";
            return false;
        }

        private static bool TryParseAt(byte[] f, long off, out BundleImage bundle)
        {
            bundle = null;
            if (off <= 0 || off + 48 >= f.Length) return false;

            var major = BitConverter.ToUInt32(f, (int)off);
            var minor = BitConverter.ToUInt32(f, (int)off + 4);
            if (!(major == 2 || major == 6) || minor != 0) return false;

            var count = BitConverter.ToInt32(f, (int)off + 8);
            if (count <= 0 || count > 20000) return false;

            long pos = off + 12;
            if (!TryReadString(f, ref pos, out var id)) return false;
            if (id.Length > 512) return false;

            long depsOff = 0, depsSize = 0, rcOff = 0, rcSize = 0;
            if (major >= 2)
            {
                if (pos + 40 > f.Length) return false;
                depsOff = BitConverter.ToInt64(f, (int)pos);
                depsSize = BitConverter.ToInt64(f, (int)pos + 8);
                rcOff = BitConverter.ToInt64(f, (int)pos + 16);
                rcSize = BitConverter.ToInt64(f, (int)pos + 24);
                pos += 40;
            }

            var list = new List<BundleEntry>(count);
            for (var i = 0; i < count; i++)
            {
                if (pos + 25 > f.Length) return false;
                var e = new BundleEntry
                {
                    Offset = BitConverter.ToInt64(f, (int)pos),
                    Size = BitConverter.ToInt64(f, (int)pos + 8),
                    CompressedSize = BitConverter.ToInt64(f, (int)pos + 16),
                    Type = f[pos + 24]
                };
                pos += 25;
                if (!TryReadString(f, ref pos, out var path)) return false;
                e.Path = path;
                if (e.Offset < 0 || e.Size < 0 || e.Offset + e.Size > off) return false;
                list.Add(e);
            }

            // 条目表紧跟在头部之后：新版布局结束于文件末尾，旧版布局恰好结束于头部起点
            if (pos != f.Length && pos != off) return false;
            if (depsOff + depsSize > off || rcOff + rcSize > off) return false;

            bundle = new BundleImage
            {
                HeaderOffset = off,
                Major = major,
                Minor = minor,
                FileCount = count,
                BundleId = id
            };
            bundle.Entries.AddRange(list);
            return true;
        }

        private static bool TryReadString(byte[] f, ref long pos, out string value)
        {
            value = null;
            if (pos >= f.Length) return false;
            long len = 0;
            var shift = 0;
            while (true)
            {
                if (pos >= f.Length || shift > 35) return false;
                var b = f[pos++];
                len |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            if (len < 0 || pos + len > f.Length) return false;
            value = Encoding.UTF8.GetString(f, (int)pos, (int)len);
            pos += len;
            return true;
        }

        private static long IndexOf(byte[] haystack, byte[] needle)
        {
            for (long i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }

    /// <summary>PE 里 RVA → 文件偏移的换算（只需要 section 表）。</summary>
    public static class PeImage
    {
        public static int RvaToOffset(PEReader pe, int rva)
        {
            foreach (var s in pe.PEHeaders.SectionHeaders)
            {
                var size = Math.Max(s.VirtualSize, s.SizeOfRawData);
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + size)
                {
                    var delta = rva - s.VirtualAddress;
                    if (delta >= s.SizeOfRawData) return -1;
                    return s.PointerToRawData + delta;
                }
            }
            return -1;
        }

        public static PEReader Open(byte[] image)
        {
            return new PEReader(ImmutableArray.Create(image));
        }

        /// <summary>读方法体头，返回 IL 起点/长度。</summary>
        public static bool TryReadBody(byte[] image, int bodyOffset, out int ilOffset, out int codeSize, out bool isFat)
        {
            ilOffset = -1;
            codeSize = 0;
            isFat = false;
            if (bodyOffset < 0 || bodyOffset >= image.Length) return false;

            var b0 = image[bodyOffset];
            if ((b0 & 0x3) == 0x2)
            {
                codeSize = b0 >> 2;
                ilOffset = bodyOffset + 1;
            }
            else if ((b0 & 0x3) == 0x3)
            {
                if (bodyOffset + 12 > image.Length) return false;
                codeSize = (int)BitConverter.ToUInt32(image, bodyOffset + 4);
                ilOffset = bodyOffset + 12;
                isFat = true;
            }
            else
            {
                return false;
            }

            if (codeSize < 2 || ilOffset + codeSize > image.Length) return false;
            return true;
        }

        public static TypeDefinitionHandle? FindType(MetadataReader md, string ns, string name)
        {
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                if (md.GetString(td.Name) != name) continue;
                if (md.GetString(td.Namespace) != ns) continue;
                return th;
            }
            return null;
        }
    }
}
