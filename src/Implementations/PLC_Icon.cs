using libplctag;
using libplctag.DataTypes.Simple;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;

namespace PlcCipTag;

/// <summary>
/// ICON PLC 的 <see cref="ITagPlc"/> 实现，继承自 <see cref="PLC_OpenAb"/>。
/// <para>
/// 针对 ICON PLC 的特殊行为进行了适配：字符串通过 <c>.LEN</c> 和 <c>.DATA</c> 子标签分别读写，
/// 浮点数组写入受单次最大长度（992 个元素）限制并自动分块。
/// </para>
/// </summary>
/// <param name="ip">PLC 的 IP 地址。</param>
/// <param name="logger">可选的日志记录器。</param>
public class PLC_Icon(string ip, ILogger? logger = null) : PLC_OpenAb(ip, logger)
{
    private const int MAX_FLOAT_ARRAY_LENGTH = 992;

    /// <summary>字符串 Tag 缓存，用于 ICON PLC 特殊的字符串读取。</summary>
    protected readonly ConcurrentDictionary<string, Tag> stringTagCache = new();

    /// <inheritdoc/>
    public override void CleanupTags()
    {
        foreach (var tag in tagCache.Values)
        {
            tag.Dispose();
        }
        foreach (var tag in stringTagCache.Values)
        {
            tag.Dispose();
        }
        tagCache.Clear();
        stringTagCache.Clear();
    }

    #region TAG

    #region String TAG

    private string ReadStringFromTag(Tag tag)
    {
        var buffer = tag.GetBuffer();
        var len = BitConverter.ToInt32(buffer, 0);

        var sb = new StringBuilder(len);
        for (int i = 4; i < 4 + len && i < buffer.Length; i++)
        {
            sb.Append((char)buffer[i]);
        }
        return sb.ToString();
    }

    private TagDint GetStringLengthTag(string address)
    {
        string chacheKey = $"stringLength{address}";
        return (TagDint)tagCache.GetOrAdd(chacheKey, _ =>
        {
            var tag = new TagDint
            {
                Name = $"{address}.LEN",
            };
            ConfigureTag(tag);
            tag.PlcType = PlcType.Omron;
            try
            {
                tag.Initialize();
                Status status = tag.GetStatus();
                if (status != Status.Ok)
                {
                    _logger?.LogWarning("初始化字符串长度标签失败: {Address}, 状态: {Status}", address, status);
                }
            }
            catch (LibPlcTagException ex)
            {
                _logger?.LogError(ex, "初始化字符串长度标签异常: {Address}", address);
            }
            return tag;
        });
    }

    private TagSint1D GetStringDataWriteTag(string address)
    {
        string cacheKey = $"stringDATA{address}";
        return (TagSint1D)tagCache.GetOrAdd(cacheKey, _ =>
        {
            var tag = new TagSint1D
            {
                Name = $"{address}.DATA",
                ArrayDimensions = new[] { 82 }
            };

            ConfigureTag(tag);
            tag.PlcType = PlcType.Omron;

            try
            {
                tag.Initialize();
                Status status = tag.GetStatus();
                if (status != Status.Ok)
                {
                    _logger?.LogWarning("初始化字符串标签失败: {Address}, 状态: {Status}", address, status);
                }
            }
            catch (LibPlcTagException ex)
            {
                _logger?.LogError(ex, "初始化字符串标签异常: {Address}", address);
            }

            return tag;
        });
    }

    private Tag GetStringDataReadTag(string address)
    {
        string cacheKey = $"string{address}";
        return stringTagCache.GetOrAdd(cacheKey, _ =>
        {
            var tag = new Tag
            {
                Name = address
            };

            ConfigureTag(tag);
            tag.PlcType = PlcType.Omron;

            try
            {
                tag.Initialize();
                Status status = tag.GetStatus();
                if (status != Status.Ok)
                {
                    _logger?.LogWarning("初始化字符串标签失败: {Address}, 状态: {Status}", address, status);
                }
            }
            catch (LibPlcTagException ex)
            {
                _logger?.LogError(ex, "初始化字符串标签异常: {Address}", address);
            }

            return tag;
        });
    }

    #endregion String TAG

    #region Float TAG

    #endregion Float TAG

    #endregion TAG

    #region Impl

    #region String Impl

    private async Task<Tag> GetStringDataReadTagAsync(string address)
    {
        string cacheKey = $"string{address}";

        // 尝试从缓存获取
        if (stringTagCache.TryGetValue(cacheKey, out var existingTag))
        {
            return existingTag;
        }

        // 创建新Tag
        var tag = new Tag
        {
            Name = address
        };
        ConfigureTag(tag);
        tag.PlcType = PlcType.Omron;

        try
        {
            await tag.InitializeAsync().ConfigureAwait(false);
            Status status = tag.GetStatus();
            if (status != Status.Ok)
            {
                _logger?.LogWarning("初始化字符串标签失败: {Address}, 状态: {Status}", address, status);
            }
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "初始化字符串标签异常: {Address}", address);
        }

        // 尝试添加到缓存
        stringTagCache.TryAdd(cacheKey, tag);

        return tag;
    }

    /// <inheritdoc/>
    public override ArraySegment<string> ReadStringArray(string address, int length)
    {
        var addressAry = new string[length];
        for (int i = 0; i < length; i++)
        {
            addressAry[i] = $"{address}[{i}]";
        }
        try
        {
            var tags = new Tag[length];
            var resultList = new string[length];
            for (int tagIndex = 0; tagIndex < length; tagIndex++)
            {
                tags[tagIndex] = GetStringDataReadTag(addressAry[tagIndex]);
            }

            for (var tagIndex = 0; tagIndex < length; tagIndex++)
            {
                tags[tagIndex].Read();
                resultList[tagIndex] = ReadStringFromTag(tags[tagIndex]);
            }

            return new ArraySegment<string>(resultList);
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "读取字符串数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public override async Task<ArraySegment<string>> ReadStringArrayAsync(string address, int length)
    {
        var addressAry = new string[length];
        for (int i = 0; i < length; i++)
        {
            addressAry[i] = $"{address}[{i}]";
        }
        try
        {
            var tags = new Tag[length];
            var resultList = new string[length];
            for (int tagIndex = 0; tagIndex < length; tagIndex++)
            {
                tags[tagIndex] = await GetStringDataReadTagAsync(addressAry[tagIndex]).ConfigureAwait(false);
            }
            for (var tagIndex = 0; tagIndex < length; tagIndex++)
            {
                await tags[tagIndex].ReadAsync().ConfigureAwait(false);
                resultList[tagIndex] = ReadStringFromTag(tags[tagIndex]);
            }
            return new ArraySegment<string>(resultList);
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "读取字符串数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public override void WriteString(string address, string writeValue)
    {
        try
        {
            var lengthTag = GetStringLengthTag(address);
            var dataTag = GetStringDataWriteTag(address);

            var header = new byte[4];
            BitConverter.GetBytes(writeValue.Length).CopyTo(header, 0);

            var data = new sbyte[82];
            byte[] bytes = Encoding.ASCII.GetBytes(writeValue);
            Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);

            lengthTag.Write(writeValue.Length);
            dataTag.Write(data);
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "写入字符串异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public override async Task WriteStringAsync(string address, string writeValue)
    {
        try
        {
            var lengthTag = GetStringLengthTag(address);
            var dataTag = GetStringDataWriteTag(address);

            var header = new byte[4];
            BitConverter.GetBytes(writeValue.Length).CopyTo(header, 0);

            var data = new sbyte[82];
            byte[] bytes = Encoding.ASCII.GetBytes(writeValue);
            Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);

            await lengthTag.WriteAsync(writeValue.Length).ConfigureAwait(false);
            await dataTag.WriteAsync(data).ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "写入字符串异常: {Address}", address);
            throw;
        }
    }

    #endregion String Impl

    #region Float Impl

    private static float[][] SplitArray(float[] source, int maxLength)
    {
        if (source == null || source.Length == 0)
        {
            return Array.Empty<float[]>();
        }

        var chunksCount = (source.Length + maxLength - 1) / maxLength;
        var result = new float[chunksCount][];

        for (int i = 0; i < chunksCount; i++)
        {
            int startIndex = i * maxLength;
            int chunkLength = Math.Min(maxLength, source.Length - startIndex);

            result[i] = new float[chunkLength];
            Array.Copy(source, startIndex, result[i], 0, chunkLength);
        }

        return result;
    }

    /// <inheritdoc/>
    public override void WriteFloatArray(string address, float[] writeValue)
    {
        var chunks = SplitArray(writeValue, MAX_FLOAT_ARRAY_LENGTH);
        for (int i = 0; i < chunks.Length; i++)
        {
            try
            {
                using var tag = new TagReal1D
                {
                    Name = $"{address}[{i * MAX_FLOAT_ARRAY_LENGTH}]",
                    ArrayDimensions = [chunks[i].Length]
                };
                ConfigureTag(tag);
                tag.Initialize();
                tag.Write(chunks[i]);
            }
            catch (LibPlcTagException ex)
            {
                _logger?.LogError(ex, "写入浮点数数组异常: {Address}", address);
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public override async Task WriteFloatArrayAsync(string address, float[] writeValue)
    {
        var chunks = SplitArray(writeValue, MAX_FLOAT_ARRAY_LENGTH);
        for (int i = 0; i < chunks.Length; i++)
        {
            try
            {
                using var tag = new TagReal1D
                {
                    Name = $"{address}[{i * MAX_FLOAT_ARRAY_LENGTH}]",
                    ArrayDimensions = [chunks[i].Length]
                };
                ConfigureTag(tag);
                await tag.InitializeAsync().ConfigureAwait(false);
                await tag.WriteAsync(chunks[i]).ConfigureAwait(false);
            }
            catch (LibPlcTagException ex)
            {
                _logger?.LogError(ex, "写入浮点数数组异常: {Address}", address);
                throw;
            }
        }
    }

    #endregion Float Impl

    #endregion Impl
}
