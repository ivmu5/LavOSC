using System.Buffers.Binary;
using System.Text;

namespace Laveyana.LavOSC.Core;

public sealed record OscMessage(string Address, string Types, IReadOnlyList<object?> Arguments, string? BundleTimeTag);

internal static class OscReader
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static List<OscMessage> Read(ReadOnlySpan<byte> packet)
    {
        var messages = new List<OscMessage>();
        ReadPacket(packet, messages, depth: 0, bundleTimeTag: null);
        return messages;
    }

    private static void ReadPacket(
        ReadOnlySpan<byte> packet, List<OscMessage> messages, int depth, string? bundleTimeTag)
    {
        if (packet.IsEmpty || packet.Length % 4 != 0)
            throw new FormatException("Размер элемента OSC должен быть кратен четырём.");
        if (depth > 16)
            throw new FormatException("Слишком много уровней вложенных OSC bundle.");

        string address = ReadString(ref packet);
        if (address == "#bundle")
        {
            string timeTag = BinaryPrimitives.ReadUInt64BigEndian(Take(ref packet, 8)).ToString("X16");
            while (!packet.IsEmpty)
            {
                int size = ReadInt32(ref packet);
                ReadPacket(Take(ref packet, size), messages, depth + 1, timeTag);
            }
            return;
        }

        if (!address.StartsWith('/'))
            throw new FormatException("Адрес OSC должен начинаться с /.");
        if (packet.IsEmpty)
        {
            messages.Add(new OscMessage(address, "", [], bundleTimeTag));
            return;
        }

        string types = ReadString(ref packet);
        if (!types.StartsWith(','))
            throw new FormatException("Список типов OSC должен начинаться с запятой.");

        var arguments = new object?[types.Length - 1];
        for (int i = 1; i < types.Length; i++)
        {
            arguments[i - 1] = types[i] switch
            {
                'i' => ReadInt32(ref packet),
                'f' => BinaryPrimitives.ReadSingleBigEndian(Take(ref packet, 4)),
                's' or 'S' => ReadString(ref packet),
                'b' => ReadBlob(ref packet),
                'h' => BinaryPrimitives.ReadInt64BigEndian(Take(ref packet, 8)),
                't' => BinaryPrimitives.ReadUInt64BigEndian(Take(ref packet, 8)).ToString("X16"),
                'd' => BinaryPrimitives.ReadDoubleBigEndian(Take(ref packet, 8)),
                'c' => ReadInt32(ref packet),
                'r' or 'm' => Convert.ToHexString(Take(ref packet, 4)),
                'T' => true,
                'F' => false,
                'N' => null,
                'I' => "Impulse",
                _ => throw new FormatException($"Неподдерживаемый тип OSC U+{(int)types[i]:X4}.")
            };
        }

        if (!packet.IsEmpty)
            throw new FormatException("После аргументов OSC остались байты.");

        messages.Add(new OscMessage(address, types, Array.AsReadOnly(arguments), bundleTimeTag));
    }

    private static int ReadInt32(ref ReadOnlySpan<byte> packet) =>
        BinaryPrimitives.ReadInt32BigEndian(Take(ref packet, 4));

    private static string ReadString(ref ReadOnlySpan<byte> packet)
    {
        int end = packet.IndexOf((byte)0);
        if (end < 0)
            throw new FormatException("Отсутствует нулевой байт в конце строки OSC.");

        return Utf8.GetString(TakePadded(ref packet, end + 1)[..end]);
    }

    private static string ReadBlob(ref ReadOnlySpan<byte> packet)
    {
        int size = ReadInt32(ref packet);
        return Convert.ToHexString(TakePadded(ref packet, size));
    }

    private static ReadOnlySpan<byte> TakePadded(ref ReadOnlySpan<byte> packet, int size)
    {
        if (size < 0 || size > packet.Length)
            throw new FormatException("Некорректная длина данных OSC.");

        var padded = Take(ref packet, (size + 3) & ~3);
        if (padded[size..].ContainsAnyExcept((byte)0))
            throw new FormatException("В выравнивании OSC обнаружены ненулевые байты.");

        return padded[..size];
    }

    private static ReadOnlySpan<byte> Take(ref ReadOnlySpan<byte> packet, int size)
    {
        if (size < 0 || size > packet.Length)
            throw new FormatException("Данные OSC закончились раньше ожидаемого.");

        var result = packet[..size];
        packet = packet[size..];
        return result;
    }
}
