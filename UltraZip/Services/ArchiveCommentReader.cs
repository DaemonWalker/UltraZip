using System.IO;
using System.Text;

namespace UltraZip.Services;

public static class ArchiveCommentReader
{
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const int EndOfCentralDirectorySize = 22;
    private const int MaximumCommentLength = 65535;

    public static string ReadZipComment(string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < EndOfCentralDirectorySize) return "";

        int tailLength = (int)Math.Min(stream.Length, EndOfCentralDirectorySize + MaximumCommentLength);
        var tail = new byte[tailLength];
        stream.Seek(-tailLength, SeekOrigin.End);
        stream.ReadExactly(tail);

        for (int i = tail.Length - EndOfCentralDirectorySize; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(tail, i) != EndOfCentralDirectorySignature) continue;
            int commentLength = BitConverter.ToUInt16(tail, i + 20);
            int commentOffset = i + EndOfCentralDirectorySize;
            if (commentOffset + commentLength > tail.Length) continue;
            if (commentLength == 0) return "";
            return DecodeComment(tail.AsSpan(commentOffset, commentLength));
        }
        throw new InvalidDataException("找不到 ZIP 中央目录结束记录，无法读取注释。");
    }

    private static string DecodeComment(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(54936).GetString(bytes);
        }
    }
}
