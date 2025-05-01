using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlcCipTag;

/// <summary>
/// Omron PLC 的 <see cref="ITagPlc"/> 实现，通过原生 CIP/EtherNet/IP 协议直接与 PLC 通信（不依赖 libplctag）。
/// <para>
/// 内部维护 TCP 连接和 CIP 会话，支持自动重连。大数组读写自动按协议限制分块
/// （单次读取上限 124 个 4 字节元素，写入上限 490 个）。
/// </para>
/// <para>
/// 支持 <c>"i="</c> 前缀的位访问地址，可对整数 Tag 进行位级读写。
/// </para>
/// </summary>
public sealed class PLC_OMRON : ITagPlc, IDisposable
{
    private const int DefaultTimeoutMilliseconds = 5000;
    private const int DefaultPort = 44818;

    private const int Max4ByteWriteCount = 490;  // 实测单次写入 float/DINT 最大元素数
    private const int Max4ByteReadCount = 124;   // 实测单次读取 float/DINT 最大元素数

    private readonly string _gateway;
    private readonly byte[] _routePath;
    private readonly TimeSpan _timeout;
    private readonly CipClient _cip;
    private readonly ILogger? _logger;

    private bool _disposedValue;

    /// <summary>
    /// 初始化 <see cref="PLC_OMRON"/> 实例并建立 CIP 客户端。
    /// </summary>
    /// <param name="ip">PLC 的 IP 地址。</param>
    /// <param name="path">
    /// CIP 路由路径，格式为逗号或分号分隔的字节值（十进制或 <c>0x</c> 十六进制），如 <c>"1,0"</c>。
    /// 用于指定通过哪些网络节点路由到达目标 PLC。
    /// </param>
    /// <param name="logger">可选的日志记录器。</param>
    public PLC_OMRON(string ip, string path = "1,0", ILogger? logger = null)
    {
        _gateway = ip;
        _routePath = ParseRoutePath(path);
        _timeout = TimeSpan.FromMilliseconds(DefaultTimeoutMilliseconds);
        _cip = new CipClient(_gateway, DefaultPort, _routePath, _timeout);
        _logger = logger;
    }

    /// <inheritdoc/>
    public void CleanupTags()
    {
        _cip.Reset();
    }

    private static byte[] ParseRoutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new byte[] { 1, 0 };
        }

        var parts = path.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>(parts.Length);
        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            int value;
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(token.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value))
                {
                    continue;
                }
            }
            else if (!int.TryParse(token, out value))
            {
                continue;
            }

            if (value < 0 || value > 255)
            {
                continue;
            }

            bytes.Add((byte)value);
        }

        if (bytes.Count == 0)
        {
            return new byte[] { 1, 0 };
        }

        return bytes.ToArray();
    }

    private static string NormalizeArrayTagName(string address, int startIndex)
    {
        return address.IndexOf('[', 0) >= 0 ? address : $"{address}[{startIndex}]";
    }

    private static bool IsAddressEndWithIndex(string address)
    {
        return Regex.IsMatch(address, "\\[[0-9]+\\]$");
    }

    private static bool TryParseBitAccessAddress(string address, out string tagName, out int bitIndex)
    {
        tagName = address;
        bitIndex = 0;

        if (!address.StartsWith("i=", StringComparison.Ordinal))
        {
            return false;
        }

        string rawAddress = address.Substring(2);
        if (!TryParseBoolBitAddress(rawAddress, out tagName, out bitIndex) || tagName == rawAddress)
        {
            return false;
        }

        return true;
    }

    private static bool TryParseBoolBitAddress(string address, out string tagName, out int bitIndex)
    {
        var bracketMatch = Regex.Match(address, @"^(.*)\[(\d+)\]$");
        if (bracketMatch.Success)
        {
            tagName = bracketMatch.Groups[1].Value;
            if (int.TryParse(bracketMatch.Groups[2].Value, out bitIndex))
            {
                return true;
            }
        }

        var dotMatch = Regex.Match(address, @"^(.*)\.(\d+)$");
        if (dotMatch.Success)
        {
            tagName = dotMatch.Groups[1].Value;
            if (int.TryParse(dotMatch.Groups[2].Value, out bitIndex))
            {
                return true;
            }
        }

        tagName = address;
        bitIndex = 0;
        return false;
    }

    private static float[] ToFloatArray(byte[] data, int count)
    {
        int available = Math.Min(count, data.Length / 4);
        var result = new float[count];
        for (int i = 0; i < available; i++)
        {
            result[i] = BitConverter.ToSingle(data, i * 4);
        }
        return result;
    }

    private static int[] ToIntArray(byte[] data, int count)
    {
        int available = Math.Min(count, data.Length / 4);
        var result = new int[count];
        for (int i = 0; i < available; i++)
        {
            result[i] = BitConverter.ToInt32(data, i * 4);
        }
        return result;
    }

    private static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ToBytes(int[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ToBytes(bool[] values)
    {
        var bytes = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i] = values[i] ? (byte)1 : (byte)0;
        }
        return bytes;
    }

    private static bool[] ToBoolArrayFromByteArray(byte[] data, int startBitIndex, int length)
    {
        if (length <= 0)
        {
            return Array.Empty<bool>();
        }

        var result = new bool[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = GetBit(data, startBitIndex + i);
        }

        return result;
    }

    private static byte[] ArrayExpandToLengthEven(byte[] data)
    {
        if (data.Length % 2 == 0)
        {
            return data;
        }

        var result = new byte[data.Length + 1];
        Buffer.BlockCopy(data, 0, result, 0, data.Length);
        return result;
    }

    private static bool IsEncapsulationLengthError(CipException ex)
    {
        return ex.Message.IndexOf("Encapsulation状态错误: 101", StringComparison.Ordinal) >= 0
            || ex.Message.IndexOf("Encapsulation状态错误: 3", StringComparison.Ordinal) >= 0;
    }

    private string ReadStringValueFromPlc(string address, Encoding encoding)
    {
        var read = _cip.Read(address, 1);
        var data = read.Data;
        if (data.Length < 2)
        {
            return string.Empty;
        }

        int count = BitConverter.ToUInt16(data, 0);
        if (count < 0)
        {
            return string.Empty;
        }

        if (count > data.Length - 2)
        {
            count = data.Length - 2;
        }

        return encoding.GetString(data, 2, count);
    }

    private async Task<string> ReadStringValueFromPlcAsync(string address, Encoding encoding)
    {
        var read = await _cip.ReadAsync(address, 1).ConfigureAwait(false);
        var data = read.Data;
        if (data.Length < 2)
        {
            return string.Empty;
        }

        int count = BitConverter.ToUInt16(data, 0);
        if (count < 0)
        {
            return string.Empty;
        }

        if (count > data.Length - 2)
        {
            count = data.Length - 2;
        }

        return encoding.GetString(data, 2, count);
    }

    private void WriteStringValueToPlc(string address, string value, Encoding encoding)
    {
        if (value == null)
        {
            value = string.Empty;
        }

        byte[] raw = encoding.GetBytes(value);
        byte[] padded = ArrayExpandToLengthEven(raw);
        var data = new byte[padded.Length + 2];
        var lenBytes = BitConverter.GetBytes((ushort)padded.Length);
        data[0] = lenBytes[0];
        data[1] = lenBytes[1];
        Buffer.BlockCopy(padded, 0, data, 2, padded.Length);
        _cip.Write(address, CipHelper.CIP_Type_String, data, 1, padBool: false);
    }

    private async Task WriteStringValueToPlcAsync(string address, string value, Encoding encoding)
    {
        if (value == null)
        {
            value = string.Empty;
        }

        byte[] raw = encoding.GetBytes(value);
        byte[] padded = ArrayExpandToLengthEven(raw);
        var data = new byte[padded.Length + 2];
        var lenBytes = BitConverter.GetBytes((ushort)padded.Length);
        data[0] = lenBytes[0];
        data[1] = lenBytes[1];
        Buffer.BlockCopy(padded, 0, data, 2, padded.Length);
        await _cip.WriteAsync(address, CipHelper.CIP_Type_String, data, 1, padBool: false).ConfigureAwait(false);
    }

    #region ITagPlc - Sync

    /// <inheritdoc/>
    public float ReadFloat(string address)
    {
        try
        {

            var read = _cip.Read(address, 1);
            if (read.Data.Length < 4)
            {
                throw new InvalidOperationException($"读取浮点失败, 数据长度不足: {read.Data.Length}");
            }
            return BitConverter.ToSingle(read.Data, 0);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "读取浮点异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public ArraySegment<float> ReadFloatArray(string address, int length)
    {
        try
        {

            if (length <= 0)
            {
                return new ArraySegment<float>(Array.Empty<float>());
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);

            if (length <= Max4ByteReadCount)
            {
                var tagName = NormalizeArrayTagName(baseName, startIndex);
                var read = _cip.Read(tagName, length);
                var result = ToFloatArray(read.Data, length);
                return new ArraySegment<float>(result, 0, result.Length);
            }

            var all = new float[length];
            int copied = 0;
            while (copied < length)
            {
                int chunkLen = Math.Min(Max4ByteReadCount, length - copied);
                var tagName = NormalizeArrayTagName(baseName, startIndex + copied);
                var read = _cip.Read(tagName, chunkLen);
                var chunk = ToFloatArray(read.Data, chunkLen);
                Array.Copy(chunk, 0, all, copied, chunk.Length);
                copied += chunkLen;
            }

            return new ArraySegment<float>(all, 0, all.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "读取浮点数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteFloat(string address, float writeValue)
    {
        try
        {

            _cip.Write(address, CipHelper.CIP_Type_Real, BitConverter.GetBytes(writeValue), 1, padBool: false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写入浮点异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteFloatArray(string address, float[] writeValue)
    {
        try
        {

            if (writeValue.Length == 0)
            {
                return;
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);

            int written = 0;
            int maxChunkLen = Max4ByteWriteCount;
            while (written < writeValue.Length)
            {
                int chunkLen = Math.Min(maxChunkLen, writeValue.Length - written);

                while (true)
                {
                    var chunk = new float[chunkLen];
                    Array.Copy(writeValue, written, chunk, 0, chunkLen);
                    var tagName = NormalizeArrayTagName(baseName, startIndex + written);

                    try
                    {
                        _cip.Write(tagName, CipHelper.CIP_Type_Real, ToBytes(chunk), chunkLen, padBool: false);
                        maxChunkLen = chunkLen;
                        written += chunkLen;
                        break;
                    }
                    catch (CipException ex) when (chunkLen > 1 && IsEncapsulationLengthError(ex))
                    {
                        chunkLen = Math.Max(1, chunkLen / 2);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写入浮点数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public ArraySegment<int> ReadDINTArray(string address, int length)
    {
        try
        {

            if (length <= 0)
            {
                return new ArraySegment<int>(Array.Empty<int>());
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);

            if (length <= Max4ByteReadCount)
            {
                var tagName = NormalizeArrayTagName(baseName, startIndex);
                var read = _cip.Read(tagName, length);
                var result = ToIntArray(read.Data, length);
                return new ArraySegment<int>(result, 0, result.Length);
            }

            var all = new int[length];
            int copied = 0;
            while (copied < length)
            {
                int chunkLen = Math.Min(Max4ByteReadCount, length - copied);
                var tagName = NormalizeArrayTagName(baseName, startIndex + copied);
                var read = _cip.Read(tagName, chunkLen);
                var chunk = ToIntArray(read.Data, chunkLen);
                Array.Copy(chunk, 0, all, copied, chunk.Length);
                copied += chunkLen;
            }

            return new ArraySegment<int>(all, 0, all.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "读取DINT数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteDINTArray(string address, int[] writeValue)
    {
        try
        {

            if (writeValue.Length == 0)
            {
                return;
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);

            if (writeValue.Length <= Max4ByteWriteCount)
            {
                var tagName = NormalizeArrayTagName(baseName, startIndex);
                _cip.Write(tagName, CipHelper.CIP_Type_DWord, ToBytes(writeValue), writeValue.Length, padBool: false);
                return;
            }

            int written = 0;
            while (written < writeValue.Length)
            {
                int chunkLen = Math.Min(Max4ByteWriteCount, writeValue.Length - written);
                var chunk = new int[chunkLen];
                Array.Copy(writeValue, written, chunk, 0, chunkLen);
                var tagName = NormalizeArrayTagName(baseName, startIndex + written);
                _cip.Write(tagName, CipHelper.CIP_Type_DWord, ToBytes(chunk), chunkLen, padBool: false);
                written += chunkLen;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写入DINT数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public ArraySegment<bool> ReadBoolArray(string address, int requestedLength)
    {
        try
        {

            if (requestedLength <= 0)
            {
                return new ArraySegment<bool>(Array.Empty<bool>());
            }

            if (TryParseBitAccessAddress(address, out var bitTagAddress, out var bitIndex))
            {
                PlcAddressHelper.TryParseArrayStartIndex(bitTagAddress, out var bitBaseName, out var bitArrayStartIndex);
                var bitTypeTagName = NormalizeArrayTagName(bitBaseName, bitArrayStartIndex);
                var bitTypeRead = _cip.Read(bitTypeTagName, 1);
                int bitLength = CipHelper.GetBitLengthForType(bitTypeRead.TypeCode);

                int elementOffset = bitIndex / bitLength;
                int bitOffset = bitIndex % bitLength;
                int readElementLength = (bitOffset + requestedLength - 1) / bitLength + 1;

                var readTagName = NormalizeArrayTagName(bitBaseName, bitArrayStartIndex + elementOffset);
                var bitRead = _cip.Read(readTagName, readElementLength);
                return new ArraySegment<bool>(ToBoolArrayFromByteArray(bitRead.Data, bitOffset, requestedLength));
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);
            var tagName = NormalizeArrayTagName(baseName, startIndex);
            var readArray = _cip.Read(tagName, requestedLength);

            if (IsAddressEndWithIndex(address))
            {
                int count = Math.Min(requestedLength, readArray.Data.Length);
                var result = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = readArray.Data[i] != 0;
                }

                return new ArraySegment<bool>(result, 0, result.Length);
            }

            return new ArraySegment<bool>(ToBoolArrayFromByteArray(readArray.Data, 0, requestedLength));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "读取布尔数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteBoolArray(string address, bool[] writeValue)
    {
        try
        {

            if (writeValue.Length == 0)
            {
                return;
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);
            var tagName = NormalizeArrayTagName(baseName, startIndex);
            _cip.Write(tagName, CipHelper.CIP_Type_Bool, ToBytes(writeValue), writeValue.Length, padBool: true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写入布尔数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteBool(string address, bool writeValue)
    {
        try
        {


            if (TryParseBitAccessAddress(address, out var bitTagAddress, out var bitIndex))
            {
                PlcAddressHelper.TryParseArrayStartIndex(bitTagAddress, out var bitBaseName, out var bitArrayStartIndex);
                var bitTypeTagName = NormalizeArrayTagName(bitBaseName, bitArrayStartIndex);
                var bitTypeRead = _cip.Read(bitTypeTagName, 1);
                int bitTypeLength = CipHelper.GetBitLengthForType(bitTypeRead.TypeCode);

                int elementOffset = bitIndex / bitTypeLength;
                int bitOffset = bitIndex % bitTypeLength;

                var targetTagName = NormalizeArrayTagName(bitBaseName, bitArrayStartIndex + elementOffset);
                var read = _cip.Read(targetTagName, 1);
                int targetBitLength = CipHelper.GetBitLengthForType(read.TypeCode);

                var data = EnsureSize(read.Data, targetBitLength / 8);
                SetBit(data, bitOffset, writeValue);
                _cip.Write(targetTagName, read.TypeCode, data, 1, padBool: read.TypeCode == CipHelper.CIP_Type_Bool);
                return;
            }

            byte[] boolBytes = writeValue ? new byte[2] { 255, 255 } : new byte[2] { 0, 0 };
            _cip.Write(address, CipHelper.CIP_Type_Bool, boolBytes, 1, padBool: true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写入布尔值异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public ArraySegment<string> ReadStringArray(string address, int length)
    {
        try
        {


            if (length <= 0)
            {
                return new ArraySegment<string>(Array.Empty<string>());
            }

            bool hasIndex = PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);
            bool treatAsArray = hasIndex || length > 1;

            if (!treatAsArray)
            {
                return new ArraySegment<string>(new[] { ReadStringValueFromPlc(address, Encoding.UTF8) });
            }

            var result = new string[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = ReadStringValueFromPlc($"{baseName}[{startIndex + i}]", Encoding.UTF8);
            }

            return new ArraySegment<string>(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "读取字符串数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteString(string address, string writeValue)
    {
        try
        {

            WriteStringValueToPlc(address, writeValue, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写入字符串异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteStringArray(string address, string[] writeValue)
    {
        try
        {
            if (writeValue.Length == 0)
            {
                return;
            }

            bool hasIndex = PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);
            bool treatAsArray = hasIndex || writeValue.Length > 1;

            if (!treatAsArray)
            {
                WriteStringValueToPlc(address, writeValue[0], Encoding.UTF8);
                return;
            }

            for (int i = 0; i < writeValue.Length; i++)
            {
                WriteStringValueToPlc($"{baseName}[{startIndex + i}]", writeValue[i], Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写入字符串数组异常: {Address}", address);
            throw;
        }
    }

    #endregion

    #region ITagPlc - Async

    /// <inheritdoc/>
    public async Task<float> ReadFloatAsync(string address)
    {
        try
        {

            var read = await _cip.ReadAsync(address, 1).ConfigureAwait(false);
            if (read.Data.Length < 4)
            {
                throw new InvalidOperationException($"读取浮点失败, 数据长度不足: {read.Data.Length}");
            }
            return BitConverter.ToSingle(read.Data, 0);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步读取浮点异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ArraySegment<float>> ReadFloatArrayAsync(string address, int length)
    {
        try
        {

            if (length <= 0)
            {
                return new ArraySegment<float>(Array.Empty<float>());
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);

            if (length <= Max4ByteReadCount)
            {
                var tagName = NormalizeArrayTagName(baseName, startIndex);
                var read = await _cip.ReadAsync(tagName, length).ConfigureAwait(false);
                var result = ToFloatArray(read.Data, length);
                return new ArraySegment<float>(result, 0, result.Length);
            }

            var all = new float[length];
            int copied = 0;
            while (copied < length)
            {
                int chunkLen = Math.Min(Max4ByteReadCount, length - copied);
                var tagName = NormalizeArrayTagName(baseName, startIndex + copied);
                var read = await _cip.ReadAsync(tagName, chunkLen).ConfigureAwait(false);
                var chunk = ToFloatArray(read.Data, chunkLen);
                Array.Copy(chunk, 0, all, copied, chunk.Length);
                copied += chunkLen;
            }

            return new ArraySegment<float>(all, 0, all.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步读取浮点数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteFloatAsync(string address, float writeValue)
    {
        try
        {

            await _cip.WriteAsync(address, CipHelper.CIP_Type_Real, BitConverter.GetBytes(writeValue), 1, padBool: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步写入浮点异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteFloatArrayAsync(string address, float[] writeValue)
    {
        if (writeValue.Length == 0)
        {
            return;
        }

        PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);

        try
        {
            int written = 0;
            int maxChunkLen = Max4ByteWriteCount;
            while (written < writeValue.Length)
            {
                int chunkLen = Math.Min(maxChunkLen, writeValue.Length - written);

                while (true)
                {
                    var chunk = new float[chunkLen];
                    Array.Copy(writeValue, written, chunk, 0, chunkLen);
                    var tagName = NormalizeArrayTagName(baseName, startIndex + written);

                    try
                    {
                        await _cip.WriteAsync(tagName, CipHelper.CIP_Type_Real, ToBytes(chunk), chunkLen, padBool: false).ConfigureAwait(false);
                        maxChunkLen = chunkLen;
                        written += chunkLen;
                        break;
                    }
                    catch (CipException ex) when (chunkLen > 1 && IsEncapsulationLengthError(ex))
                    {
                        chunkLen = Math.Max(1, chunkLen / 2);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步写入浮点数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ArraySegment<int>> ReadDINTArrayAsync(string address, int length)
    {
        try
        {

            if (length <= 0)
            {
                return new ArraySegment<int>(Array.Empty<int>());
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);

            if (length <= Max4ByteReadCount)
            {
                var tagName = NormalizeArrayTagName(baseName, startIndex);
                var read = await _cip.ReadAsync(tagName, length).ConfigureAwait(false);
                var result = ToIntArray(read.Data, length);
                return new ArraySegment<int>(result, 0, result.Length);
            }

            var all = new int[length];
            int copied = 0;
            while (copied < length)
            {
                int chunkLen = Math.Min(Max4ByteReadCount, length - copied);
                var tagName = NormalizeArrayTagName(baseName, startIndex + copied);
                var read = await _cip.ReadAsync(tagName, chunkLen).ConfigureAwait(false);
                var chunk = ToIntArray(read.Data, chunkLen);
                Array.Copy(chunk, 0, all, copied, chunk.Length);
                copied += chunkLen;
            }

            return new ArraySegment<int>(all, 0, all.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步读取DINT数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteDINTArrayAsync(string address, int[] writeValue)
    {
        try
        {

            if (writeValue.Length == 0)
            {
                return;
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);

            if (writeValue.Length <= Max4ByteWriteCount)
            {
                var tagName = NormalizeArrayTagName(baseName, startIndex);
                await _cip.WriteAsync(tagName, CipHelper.CIP_Type_DWord, ToBytes(writeValue), writeValue.Length, padBool: false).ConfigureAwait(false);
                return;
            }

            int written = 0;
            while (written < writeValue.Length)
            {
                int chunkLen = Math.Min(Max4ByteWriteCount, writeValue.Length - written);
                var chunk = new int[chunkLen];
                Array.Copy(writeValue, written, chunk, 0, chunkLen);
                var tagName = NormalizeArrayTagName(baseName, startIndex + written);
                await _cip.WriteAsync(tagName, CipHelper.CIP_Type_DWord, ToBytes(chunk), chunkLen, padBool: false).ConfigureAwait(false);
                written += chunkLen;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步写入DINT数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ArraySegment<bool>> ReadBoolArrayAsync(string address, int requestedLength)
    {
        try
        {

            if (requestedLength <= 0)
            {
                return new ArraySegment<bool>(Array.Empty<bool>());
            }

            if (TryParseBitAccessAddress(address, out var bitTagAddress, out var bitIndex))
            {
                PlcAddressHelper.TryParseArrayStartIndex(bitTagAddress, out var bitBaseName, out var bitArrayStartIndex);
                var bitTypeTagName = NormalizeArrayTagName(bitBaseName, bitArrayStartIndex);
                var bitTypeRead = await _cip.ReadAsync(bitTypeTagName, 1).ConfigureAwait(false);
                int bitLength = CipHelper.GetBitLengthForType(bitTypeRead.TypeCode);

                int elementOffset = bitIndex / bitLength;
                int bitOffset = bitIndex % bitLength;
                int readElementLength = (bitOffset + requestedLength - 1) / bitLength + 1;

                var readTagName = NormalizeArrayTagName(bitBaseName, bitArrayStartIndex + elementOffset);
                var bitRead = await _cip.ReadAsync(readTagName, readElementLength).ConfigureAwait(false);
                return new ArraySegment<bool>(ToBoolArrayFromByteArray(bitRead.Data, bitOffset, requestedLength));
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);
            var tagName = NormalizeArrayTagName(baseName, startIndex);
            var readArray = await _cip.ReadAsync(tagName, requestedLength).ConfigureAwait(false);

            if (IsAddressEndWithIndex(address))
            {
                int count = Math.Min(requestedLength, readArray.Data.Length);
                var result = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = readArray.Data[i] != 0;
                }

                return new ArraySegment<bool>(result, 0, result.Length);
            }

            return new ArraySegment<bool>(ToBoolArrayFromByteArray(readArray.Data, 0, requestedLength));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步读取布尔数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteBoolArrayAsync(string address, bool[] writeValue)
    {
        try
        {

            if (writeValue.Length == 0)
            {
                return;
            }

            PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);
            var tagName = NormalizeArrayTagName(baseName, startIndex);
            await _cip.WriteAsync(tagName, CipHelper.CIP_Type_Bool, ToBytes(writeValue), writeValue.Length, padBool: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步写入布尔数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteBoolAsync(string address, bool writeValue)
    {
        try
        {


            if (TryParseBitAccessAddress(address, out var bitTagAddress, out var bitIndex))
            {
                PlcAddressHelper.TryParseArrayStartIndex(bitTagAddress, out var bitBaseName, out var bitArrayStartIndex);
                var bitTypeTagName = NormalizeArrayTagName(bitBaseName, bitArrayStartIndex);
                var bitTypeRead = await _cip.ReadAsync(bitTypeTagName, 1).ConfigureAwait(false);
                int bitTypeLength = CipHelper.GetBitLengthForType(bitTypeRead.TypeCode);

                int elementOffset = bitIndex / bitTypeLength;
                int bitOffset = bitIndex % bitTypeLength;

                var targetTagName = NormalizeArrayTagName(bitBaseName, bitArrayStartIndex + elementOffset);
                var read = await _cip.ReadAsync(targetTagName, 1).ConfigureAwait(false);
                int targetBitLength = CipHelper.GetBitLengthForType(read.TypeCode);

                var data = EnsureSize(read.Data, targetBitLength / 8);
                SetBit(data, bitOffset, writeValue);
                await _cip.WriteAsync(targetTagName, read.TypeCode, data, 1, padBool: read.TypeCode == CipHelper.CIP_Type_Bool).ConfigureAwait(false);
                return;
            }

            byte[] boolBytes = writeValue ? new byte[2] { 255, 255 } : new byte[2] { 0, 0 };
            await _cip.WriteAsync(address, CipHelper.CIP_Type_Bool, boolBytes, 1, padBool: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步写入布尔值异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ArraySegment<string>> ReadStringArrayAsync(string address, int length)
    {
        try
        {


            if (length <= 0)
            {
                return new ArraySegment<string>(Array.Empty<string>());
            }

            bool hasIndex = PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);
            bool treatAsArray = hasIndex || length > 1;

            if (!treatAsArray)
            {
                return new ArraySegment<string>(new[] { await ReadStringValueFromPlcAsync(address, Encoding.UTF8).ConfigureAwait(false) });
            }

            var result = new string[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = await ReadStringValueFromPlcAsync($"{baseName}[{startIndex + i}]", Encoding.UTF8).ConfigureAwait(false);
            }

            return new ArraySegment<string>(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步读取字符串数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteStringAsync(string address, string writeValue)
    {
        try
        {

            await WriteStringValueToPlcAsync(address, writeValue, Encoding.UTF8).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步写入字符串异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteStringArrayAsync(string address, string[] writeValue)
    {
        try
        {
            if (writeValue.Length == 0)
            {
                return;
            }

            bool hasIndex = PlcAddressHelper.TryParseArrayStartIndex(address, out var baseName, out var startIndex);
            bool treatAsArray = hasIndex || writeValue.Length > 1;

            if (!treatAsArray)
            {
                await WriteStringValueToPlcAsync(address, writeValue[0], Encoding.UTF8).ConfigureAwait(false);
                return;
            }

            for (int i = 0; i < writeValue.Length; i++)
            {
                await WriteStringValueToPlcAsync($"{baseName}[{startIndex + i}]", writeValue[i], Encoding.UTF8).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "异步写入字符串数组异常: {Address}", address);
            throw;
        }
    }

    #endregion

    private static byte[] EnsureSize(byte[] data, int length)
    {
        if (data.Length >= length)
        {
            return data;
        }

        var result = new byte[length];
        Buffer.BlockCopy(data, 0, result, 0, data.Length);
        return result;
    }

    private static bool GetBit(byte[] data, int bitIndex)
    {
        int byteIndex = bitIndex / 8;
        int bit = bitIndex % 8;
        if (byteIndex >= data.Length)
        {
            return false;
        }
        return (data[byteIndex] & (1 << bit)) != 0;
    }

    private static void SetBit(byte[] data, int bitIndex, bool value)
    {
        int byteIndex = bitIndex / 8;
        int bit = bitIndex % 8;
        if (byteIndex >= data.Length)
        {
            return;
        }
        if (value)
        {
            data[byteIndex] |= (byte)(1 << bit);
        }
        else
        {
            data[byteIndex] &= (byte)~(1 << bit);
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposedValue)
        {
            return;
        }

        if (disposing)
        {
            CleanupTags();
            _cip.Dispose();
        }

        _disposedValue = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private sealed class CipClient : IDisposable
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly byte[] _routePath;
        private readonly TimeSpan _timeout;
        private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);

        private TcpClient? _client;
        private NetworkStream? _stream;
        private uint _sessionHandle;
        private long _contextId;
        private bool _disposed;

        public CipClient(string ip, int port, byte[] routePath, TimeSpan timeout)
        {
            _ip = ip;
            _port = port;
            _routePath = routePath ?? Array.Empty<byte>();
            _timeout = timeout;
        }

        public CipDataResult Read(string address, int length)
        {
            byte[] cip = CipHelper.PackRequestRead(address, length);
            return SendAndParse(cip, isRead: true);
        }

        public Task<CipDataResult> ReadAsync(string address, int length)
        {
            byte[] cip = CipHelper.PackRequestRead(address, length);
            return SendAndParseAsync(cip, isRead: true);
        }

        public void Write(string address, ushort typeCode, byte[] value, int length, bool padBool)
        {
            byte[] cip = CipHelper.PackRequestWrite(address, typeCode, value, length, isConnectedAddress: false, paddingTail: padBool);
            SendAndParse(cip, isRead: false);
        }

        public async Task WriteAsync(string address, ushort typeCode, byte[] value, int length, bool padBool)
        {
            byte[] cip = CipHelper.PackRequestWrite(address, typeCode, value, length, isConnectedAddress: false, paddingTail: padBool);
            await SendAndParseAsync(cip, isRead: false).ConfigureAwait(false);
        }

        public void Reset()
        {
            _ioLock.Wait();
            try
            {
                Close();
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private CipDataResult SendAndParse(byte[] cip, bool isRead)
        {
            _ioLock.Wait();
            try
            {
                EnsureConnected();
                byte[] response = SendEncapsulated(cip);
                try
                {
                    return CipHelper.ExtractActualData(response, isRead);
                }
                catch (CipException ex) when (ex.Message.Contains("Encapsulation状态错误"))
                {
                    // 封装层错误，可能是会话过期，尝试重新连接
                    Close();
                    EnsureConnected();
                    response = SendEncapsulated(cip);
                    return CipHelper.ExtractActualData(response, isRead);
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private async Task<CipDataResult> SendAndParseAsync(byte[] cip, bool isRead)
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(false);
                byte[] response = await SendEncapsulatedAsync(cip).ConfigureAwait(false);
                try
                {
                    return CipHelper.ExtractActualData(response, isRead);
                }
                catch (CipException ex) when (ex.Message.Contains("Encapsulation状态错误"))
                {
                    // 封装层错误，可能是会话过期，尝试重新连接
                    Close();
                    await EnsureConnectedAsync().ConfigureAwait(false);
                    response = await SendEncapsulatedAsync(cip).ConfigureAwait(false);
                    return CipHelper.ExtractActualData(response, isRead);
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private void EnsureConnected()
        {
            if (_client != null && _client.Connected && _stream != null)
            {
                return;
            }

            Close();

            _client = new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = (int)_timeout.TotalMilliseconds,
                SendTimeout = (int)_timeout.TotalMilliseconds
            };

            var connectTask = _client.ConnectAsync(_ip, _port);
            if (!connectTask.Wait(_timeout))
            {
                Close();
                throw new TimeoutException("PLC连接超时");
            }
            if (connectTask.IsFaulted)
            {
                Close();
                throw connectTask.Exception?.GetBaseException() ?? new SocketException();
            }

            _stream = _client.GetStream();
            RegisterSession();
        }

        private async Task EnsureConnectedAsync()
        {
            if (_client != null && _client.Connected && _stream != null)
            {
                return;
            }

            Close();

            _client = new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = (int)_timeout.TotalMilliseconds,
                SendTimeout = (int)_timeout.TotalMilliseconds
            };

            var connectTask = _client.ConnectAsync(_ip, _port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(_timeout)).ConfigureAwait(false);
            if (completed != connectTask)
            {
                Close();
                throw new TimeoutException("PLC连接超时");
            }
            await connectTask.ConfigureAwait(false);

            _stream = _client.GetStream();
            await RegisterSessionAsync().ConfigureAwait(false);
        }

        private void RegisterSession()
        {
            byte[] commandSpecific = new byte[4] { 1, 0, 0, 0 };
            byte[] senderContext = BitConverter.GetBytes(Interlocked.Increment(ref _contextId));
            byte[] request = CipHelper.PackRequestHeader(0x65, 0, commandSpecific, senderContext);
            byte[] response = SendRaw(request);
            int status = BitConverter.ToInt32(response, 8);
            if (status != 0)
            {
                throw new CipException($"RegisterSession failed: {status}");
            }
            _sessionHandle = BitConverter.ToUInt32(response, 4);
        }

        private async Task RegisterSessionAsync()
        {
            byte[] commandSpecific = new byte[4] { 1, 0, 0, 0 };
            byte[] senderContext = BitConverter.GetBytes(Interlocked.Increment(ref _contextId));
            byte[] request = CipHelper.PackRequestHeader(0x65, 0, commandSpecific, senderContext);
            byte[] response = await SendRawAsync(request).ConfigureAwait(false);
            int status = BitConverter.ToInt32(response, 8);
            if (status != 0)
            {
                throw new CipException($"RegisterSession failed: {status}");
            }
            _sessionHandle = BitConverter.ToUInt32(response, 4);
        }

        private byte[] SendEncapsulated(byte[] cip)
        {
            byte[] commandSpecific = CipHelper.PackCommandSpecificData(new byte[4], CipHelper.PackCommandService(_routePath, cip));
            byte[] senderContext = BitConverter.GetBytes(Interlocked.Increment(ref _contextId));
            byte[] request = CipHelper.PackRequestHeader(0x6F, _sessionHandle, commandSpecific, senderContext);
            return SendRaw(request);
        }

        private async Task<byte[]> SendEncapsulatedAsync(byte[] cip)
        {
            byte[] commandSpecific = CipHelper.PackCommandSpecificData(new byte[4], CipHelper.PackCommandService(_routePath, cip));
            byte[] senderContext = BitConverter.GetBytes(Interlocked.Increment(ref _contextId));
            byte[] request = CipHelper.PackRequestHeader(0x6F, _sessionHandle, commandSpecific, senderContext);
            return await SendRawAsync(request).ConfigureAwait(false);
        }

        private byte[] SendRaw(byte[] request)
        {
            if (_stream == null)
            {
                throw new InvalidOperationException("PLC未连接");
            }

            _stream.Write(request, 0, request.Length);
            var response = ReadEncapResponse();

            return response;
        }

        private async Task<byte[]> SendRawAsync(byte[] request)
        {
            if (_stream == null)
            {
                throw new InvalidOperationException("PLC未连接");
            }

            using var cts = new CancellationTokenSource(_timeout);
            await _stream.WriteAsync(request, 0, request.Length, cts.Token).ConfigureAwait(false);
            return await ReadEncapResponseAsync(cts.Token).ConfigureAwait(false);
        }

        private byte[] ReadEncapResponse()
        {
            if (_stream == null)
            {
                throw new InvalidOperationException("PLC未连接");
            }

            byte[] header = ReadExact(_stream, 24);
            int length = BitConverter.ToUInt16(header, 2);
            byte[] payload = length > 0 ? ReadExact(_stream, length) : Array.Empty<byte>();
            var response = new byte[24 + payload.Length];
            Buffer.BlockCopy(header, 0, response, 0, 24);
            if (payload.Length > 0)
            {
                Buffer.BlockCopy(payload, 0, response, 24, payload.Length);
            }
            return response;
        }

        private async Task<byte[]> ReadEncapResponseAsync(CancellationToken token)
        {
            if (_stream == null)
            {
                throw new InvalidOperationException("PLC未连接");
            }

            byte[] header = await ReadExactAsync(_stream, 24, token).ConfigureAwait(false);
            int length = BitConverter.ToUInt16(header, 2);
            byte[] payload = length > 0 ? await ReadExactAsync(_stream, length, token).ConfigureAwait(false) : Array.Empty<byte>();
            var response = new byte[24 + payload.Length];
            Buffer.BlockCopy(header, 0, response, 0, 24);
            if (payload.Length > 0)
            {
                Buffer.BlockCopy(payload, 0, response, 24, payload.Length);
            }
            return response;
        }

        private static byte[] ReadExact(NetworkStream stream, int length)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(buffer, offset, length - offset);
                if (read == 0)
                {
                    throw new InvalidOperationException("PLC连接已关闭");
                }
                offset += read;
            }
            return buffer;
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken token)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(buffer, offset, length - offset, token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidOperationException("PLC连接已关闭");
                }
                offset += read;
            }
            return buffer;
        }

        private void Close()
        {
            if (_stream != null)
            {
                try
                {
                    if (_sessionHandle != 0)
                    {
                        byte[] request = CipHelper.PackRequestHeader(0x66, _sessionHandle, Array.Empty<byte>());
                        _stream.Write(request, 0, request.Length);
                        _ = ReadEncapResponse();
                    }
                }
                catch
                {
                    // ignore
                }
            }

            _stream?.Dispose();
            _client?.Close();
            _stream = null;
            _client = null;
            _sessionHandle = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Reset();
            }
            catch
            {
                // ignore
            }

            _ioLock.Dispose();
        }
    }

    private readonly struct CipDataResult
    {
        public CipDataResult(byte[] data, ushort typeCode, bool hasMore)
        {
            Data = data;
            TypeCode = typeCode;
            HasMore = hasMore;
        }

        public byte[] Data { get; }
        public ushort TypeCode { get; }
        public bool HasMore { get; }
    }

    private sealed class CipException : Exception
    {
        public CipException(string message) : base(message)
        {
        }
    }

    private static class CipHelper
    {
        public const ushort CIP_Type_Bool = 193;
        public const ushort CIP_Type_Byte = 194;
        public const ushort CIP_Type_Word = 195;
        public const ushort CIP_Type_DWord = 196;
        public const ushort CIP_Type_LInt = 197;
        public const ushort CIP_Type_USInt = 198;
        public const ushort CIP_Type_UInt = 199;
        public const ushort CIP_Type_UDint = 200;
        public const ushort CIP_Type_ULint = 201;
        public const ushort CIP_Type_Real = 202;
        public const ushort CIP_Type_Double = 203;
        public const ushort CIP_Type_String = 208;
        public const ushort CIP_Type_D1 = 209;
        public const ushort CIP_Type_D2 = 210;
        public const ushort CIP_Type_D3 = 211;
        public const ushort CIP_Type_D4 = 212;

        public static int GetBitLengthForType(ushort typeCode)
        {
            if (typeCode == CIP_Type_Byte || typeCode == CIP_Type_D1 || typeCode == CIP_Type_USInt)
            {
                return 8;
            }
            if (typeCode == CIP_Type_Word || typeCode == CIP_Type_UInt || typeCode == CIP_Type_D2)
            {
                return 16;
            }
            if (typeCode == CIP_Type_DWord || typeCode == CIP_Type_UDint || typeCode == CIP_Type_Real || typeCode == CIP_Type_D3)
            {
                return 32;
            }
            if (typeCode == CIP_Type_LInt || typeCode == CIP_Type_ULint || typeCode == CIP_Type_Double || typeCode == CIP_Type_D4)
            {
                return 64;
            }
            return 32;
        }

        public static byte[] PackRequestHeader(ushort command, uint session, byte[] commandSpecificData, byte[]? senderContext = null)
        {
            if (commandSpecificData == null)
            {
                commandSpecificData = Array.Empty<byte>();
            }

            byte[] buffer = new byte[24 + commandSpecificData.Length];
            BitConverter.GetBytes(command).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)commandSpecificData.Length).CopyTo(buffer, 2);
            BitConverter.GetBytes(session).CopyTo(buffer, 4);
            if (senderContext != null)
            {
                int copyLen = Math.Min(senderContext.Length, 8);
                Buffer.BlockCopy(senderContext, 0, buffer, 12, copyLen);
            }
            Buffer.BlockCopy(commandSpecificData, 0, buffer, 24, commandSpecificData.Length);
            return buffer;
        }

        public static byte[] PackCommandSpecificData(params byte[][] service)
        {
            using var memoryStream = new System.IO.MemoryStream();
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(10);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(BitConverter.GetBytes(service.Length)[0]);
            memoryStream.WriteByte(BitConverter.GetBytes(service.Length)[1]);
            for (int i = 0; i < service.Length; i++)
            {
                memoryStream.Write(service[i], 0, service[i].Length);
            }
            return memoryStream.ToArray();
        }

        public static byte[] PackCommandService(byte[] portSlot, params byte[][] cips)
        {
            using var memoryStream = new System.IO.MemoryStream();
            memoryStream.WriteByte(178);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(82);
            memoryStream.WriteByte(2);
            memoryStream.WriteByte(32);
            memoryStream.WriteByte(6);
            memoryStream.WriteByte(36);
            memoryStream.WriteByte(1);
            memoryStream.WriteByte(10);
            memoryStream.WriteByte(240);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(0);

            int num = 0;
            if (cips.Length == 1)
            {
                memoryStream.Write(cips[0], 0, cips[0].Length);
                num += cips[0].Length;
                if (cips[0].Length % 2 == 1)
                {
                    memoryStream.WriteByte(0);
                }
            }
            else
            {
                memoryStream.WriteByte(10);
                memoryStream.WriteByte(2);
                memoryStream.WriteByte(32);
                memoryStream.WriteByte(2);
                memoryStream.WriteByte(36);
                memoryStream.WriteByte(1);
                num += 8;
                memoryStream.Write(BitConverter.GetBytes((ushort)cips.Length), 0, 2);
                ushort offset = (ushort)(2 + 2 * cips.Length);
                num += 2 * cips.Length;
                for (int i = 0; i < cips.Length; i++)
                {
                    memoryStream.Write(BitConverter.GetBytes(offset), 0, 2);
                    offset = (ushort)(offset + cips[i].Length);
                }
                for (int j = 0; j < cips.Length; j++)
                {
                    memoryStream.Write(cips[j], 0, cips[j].Length);
                    num += cips[j].Length;
                }
            }

            if (portSlot != null)
            {
                memoryStream.WriteByte((byte)((portSlot.Length + 1) / 2));
                memoryStream.WriteByte(0);
                memoryStream.Write(portSlot, 0, portSlot.Length);
                if (portSlot.Length % 2 == 1)
                {
                    memoryStream.WriteByte(0);
                }
            }

            byte[] array = memoryStream.ToArray();
            BitConverter.GetBytes((short)num).CopyTo(array, 12);
            BitConverter.GetBytes((short)(array.Length - 4)).CopyTo(array, 2);
            return array;
        }

        public static byte[] PackRequestRead(string address, int length, bool isConnectedAddress = false)
        {
            byte[] buffer = new byte[1024];
            int index = 0;
            buffer[index++] = 76;
            index++;
            byte[] path = BuildRequestPathCommand(address, isConnectedAddress);
            Buffer.BlockCopy(path, 0, buffer, index, path.Length);
            index += path.Length;
            buffer[1] = (byte)((index - 2) / 2);
            buffer[index++] = BitConverter.GetBytes(length)[0];
            buffer[index++] = BitConverter.GetBytes(length)[1];
            byte[] result = new byte[index];
            Array.Copy(buffer, 0, result, 0, index);
            return result;
        }

        public static byte[] PackRequestWrite(string address, ushort typeCode, byte[] value, int length = 1, bool isConnectedAddress = false, bool paddingTail = false)
        {
            byte[] buffer = new byte[1024];
            int index = 0;
            buffer[index++] = 77;  // Write Service Code
            index++;  // 路径大小占位符
            byte[] path = BuildRequestPathCommand(address, isConnectedAddress);
            Buffer.BlockCopy(path, 0, buffer, index, path.Length);
            index += path.Length;
            buffer[1] = (byte)((index - 2) / 2);  // 路径大小（以 word 为单位）
            buffer[index++] = BitConverter.GetBytes(typeCode)[0];
            buffer[index++] = BitConverter.GetBytes(typeCode)[1];
            buffer[index++] = BitConverter.GetBytes(length)[0];
            buffer[index++] = BitConverter.GetBytes(length)[1];
            if (value == null)
            {
                value = Array.Empty<byte>();
            }
            // 对于 Bool 类型且长度为1，如果值长度为奇数需要填充
            int tail = 0;
            if (paddingTail && typeCode == CIP_Type_Bool && length == 1 && value.Length % 2 > 0)
            {
                tail = 1;
            }
            byte[] result = new byte[value.Length + index + tail];
            Array.Copy(buffer, 0, result, 0, index);
            Buffer.BlockCopy(value, 0, result, index, value.Length);
            // 填充字节已经是 0（默认值），不需要额外操作
            return result;
        }

        public static CipDataResult ExtractActualData(byte[] response, bool isRead)
        {
            // AllenBradleyHelper.ExtractActualDataList 的实现
            // 最小响应长度：24字节封装头 + 一些命令特定数据
            if (response.Length < 24)
            {
                throw new CipException($"响应长度过短: {response.Length} 字节");
            }

            int encapsulationStatus = BitConverter.ToInt32(response, 8);
            if (encapsulationStatus != 0)
            {
                throw new CipException($"Encapsulation状态错误: {encapsulationStatus}");
            }

            // 对于写入操作，响应可能比读取操作短
            if (!isRead)
            {
                // 写入响应的最小长度检查
                // 封装头(24) + 接口句柄(4) + 超时(2) + 项计数(2) + 地址项(4) + 数据项头(4) = 40
                // 但有时候可能更短，取决于实际响应
                if (response.Length >= 42)
                {
                    // response[40] 是服务码 (Write Reply = 0xCD = 205)
                    // response[42] 是通用状态
                    byte generalStatus = response[42];
                    if (generalStatus != 0 && generalStatus != 6)
                    {
                        // 状态码 6 表示部分传输，某些情况下可以接受
                        throw new CipException($"CIP写入失败, 状态码: {generalStatus}");
                    }
                }
                else if (response.Length >= 40)
                {
                    // 某些情况下，写入成功响应可能只有40字节
                    // 检查服务码
                    byte serviceCode = response[40];
                    // 0xCD (205) = Write Reply, 0xD3 (211) = Write Fragment Reply
                    if (serviceCode != 0xCD && serviceCode != 0xD3 && serviceCode != 0xCE)
                    {
                        // 如果不是标准写入响应码，尝试检查是否有错误
                        if (response.Length > 42)
                        {
                            byte status = response[42];
                            if (status != 0)
                            {
                                throw new CipException($"CIP写入失败, 状态码: {status}");
                            }
                        }
                    }
                }
                return new CipDataResult(Array.Empty<byte>(), 0, false);
            }

            // 读取操作需要更多数据
            if (response.Length < 44)
            {
                throw new CipException($"读取响应长度过短: {response.Length} 字节");
            }

            byte service = response[40];
            // 0x8A = 多服务响应
            if (service == 0x8A)
            {
                throw new CipException("不支持的多服务响应");
            }

            byte cipStatus = response[42];
            // 状态码 0 = 成功, 6 = 部分数据（有更多数据需要读取）
            if (cipStatus != 0 && cipStatus != 6)
            {
                throw new CipException($"CIP读取失败, 状态码: {cipStatus}");
            }

            bool hasMore = cipStatus == 6;

            if (response.Length < 46)
            {
                // 如果响应太短，返回空数据
                return new CipDataResult(Array.Empty<byte>(), 0, hasMore);
            }

            ushort itemLength = BitConverter.ToUInt16(response, 38);
            ushort typeCode = BitConverter.ToUInt16(response, 44);
            int dataStart = 46;
            int dataLength = itemLength - 6;
            if (dataLength < 0)
            {
                dataLength = 0;
            }
            if (dataStart + dataLength > response.Length)
            {
                dataLength = Math.Max(0, response.Length - dataStart);
            }

            var data = new byte[dataLength];
            if (dataLength > 0)
            {
                Buffer.BlockCopy(response, dataStart, data, 0, dataLength);
            }

            return new CipDataResult(data, typeCode, hasMore);
        }

        private static byte[] BuildRequestPathCommand(string address, bool isConnectedAddress = false)
        {
            using var memoryStream = new System.IO.MemoryStream();
            string[] segments = address.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                string indexPart = string.Empty;
                int openIndex = segment.IndexOf('[');
                int closeIndex = segment.LastIndexOf(']');
                if (openIndex > 0 && closeIndex > openIndex)
                {
                    indexPart = segment.Substring(openIndex + 1, closeIndex - openIndex - 1);
                    segment = segment.Substring(0, openIndex);
                }

                memoryStream.WriteByte(145);
                byte[] nameBytes = Encoding.UTF8.GetBytes(segment);
                memoryStream.WriteByte((byte)nameBytes.Length);
                memoryStream.Write(nameBytes, 0, nameBytes.Length);
                if (nameBytes.Length % 2 == 1)
                {
                    memoryStream.WriteByte(0);
                }

                if (string.IsNullOrEmpty(indexPart))
                {
                    continue;
                }

                MatchCollection matches = Regex.Matches(indexPart, "[0-9]+");
                for (int j = 0; j < matches.Count; j++)
                {
                    int idx = Convert.ToInt32(matches[j].Value);
                    if (idx < 256 && !isConnectedAddress)
                    {
                        memoryStream.WriteByte(40);
                        memoryStream.WriteByte((byte)idx);
                    }
                    else if (idx < 65536)
                    {
                        memoryStream.WriteByte(41);
                        memoryStream.WriteByte(0);
                        memoryStream.WriteByte(BitConverter.GetBytes(idx)[0]);
                        memoryStream.WriteByte(BitConverter.GetBytes(idx)[1]);
                    }
                    else
                    {
                        memoryStream.WriteByte(42);
                        memoryStream.WriteByte(0);
                        memoryStream.Write(BitConverter.GetBytes(idx), 0, 4);
                    }
                }
            }

            return memoryStream.ToArray();
        }
    }
}
