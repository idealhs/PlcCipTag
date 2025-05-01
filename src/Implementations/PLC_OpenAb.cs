using libplctag;
using libplctag.DataTypes;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace PlcCipTag;

/// <summary>
/// Allen-Bradley ControlLogix PLC 的 <see cref="ITagPlc"/> 实现，基于 libplctag.NET 库通过 EtherNet/IP 协议通信。
/// <para>Tag 实例会被内部缓存和复用，通过 <see cref="CleanupTags"/> 或 <see cref="IDisposable.Dispose"/> 释放。</para>
/// </summary>
public class PLC_OpenAb : ITagPlc, IDisposable
{
    private const int DEFAULT_PORT = 44818; // 标准EtherNet/IP端口
    private const int DEFAULT_TIMEOUT = 5000; // 默认超时时间

    /// <summary>Tag 实例缓存，键为地址+类型组合的缓存键。</summary>
    protected readonly ConcurrentDictionary<string, ITag> tagCache = new();
    /// <summary>原始布尔 Tag 缓存，用于位级 read-modify-write 操作。</summary>
    protected readonly ConcurrentDictionary<string, Tag> rawBoolTagCache = new();
    /// <summary>日志记录器。</summary>
    protected readonly ILogger? _logger;

    /// <summary>PLC 的 IP 地址。</summary>
    protected string _gateway;
    /// <summary>通信超时时间。</summary>
    protected TimeSpan _timeout = TimeSpan.FromMilliseconds(DEFAULT_TIMEOUT);
    private bool _disposedValue;

    /// <summary>
    /// 初始化 <see cref="PLC_OpenAb"/> 实例。
    /// </summary>
    /// <param name="ip">PLC 的 IP 地址，如 <c>"192.168.1.100"</c>。</param>
    /// <param name="logger">可选的日志记录器，用于记录通信异常。</param>
    public PLC_OpenAb(string ip, ILogger? logger = null)
    {
        _gateway = ip;
        _logger = logger;
    }

    /// <inheritdoc/>
    public virtual void CleanupTags()
    {
        foreach (var tag in tagCache.Values)
        {
            tag.Dispose();
        }
        tagCache.Clear();
        foreach (var tag in rawBoolTagCache.Values)
        {
            tag.Dispose();
        }
        rawBoolTagCache.Clear();
    }

    // 配置Tag的通用属性
    /// <summary>为 <see cref="ITag"/> 实例配置网关、路径、PLC 类型和超时等通信参数。</summary>
    protected void ConfigureTag(ITag tag)
    {
        tag.Gateway = _gateway;
        tag.Path = "1,0"; // 默认路径，可能需要根据实际PLC设置调整
        tag.PlcType = PlcType.ControlLogix;
        tag.Protocol = Protocol.ab_eip;
        tag.Timeout = _timeout;
    }

    /// <summary>为 <see cref="Tag"/> 实例配置网关、路径、PLC 类型和超时等通信参数。</summary>
    protected void ConfigureTag(Tag tag)
    {
        tag.Gateway = _gateway;
        tag.Path = "1,0"; // 默认路径，可能需要根据实际PLC设置调整
        tag.PlcType = PlcType.ControlLogix;
        tag.Protocol = Protocol.ab_eip;
        tag.Timeout = _timeout;
    }

    #region 获取Tag实例的辅助方法

    private Tag<TMapper, TValue> GetTag<TMapper, TValue>(string cacheKey, string address, int[]? arrayDimensions = null)
        where TMapper : IPlcMapper<TValue>, new()
    {
        return (Tag<TMapper, TValue>)tagCache.GetOrAdd(cacheKey, _ =>
        {
            var newTag = new Tag<TMapper, TValue> { Name = address };
            if (arrayDimensions != null)
            {
                newTag.ArrayDimensions = arrayDimensions;
            }
            ConfigureTag(newTag);
            try
            {
                newTag.Initialize();
                Status status = newTag.GetStatus();
                if (status != Status.Ok)
                {
                    _logger?.LogWarning("初始化标签失败: {Address}, 状态: {Status}", address, status);
                }
            }
            catch (LibPlcTagException ex)
            {
                _logger?.LogError(ex, "初始化标签异常: {Address}", address);
                throw;
            }
            return newTag;
        });
    }

    private async Task<Tag<TMapper, TValue>> GetTagAsync<TMapper, TValue>(string cacheKey, string address, int[]? arrayDimensions = null)
        where TMapper : IPlcMapper<TValue>, new()
    {
        if (tagCache.TryGetValue(cacheKey, out var existingTag))
        {
            return (Tag<TMapper, TValue>)existingTag;
        }

        var newTag = new Tag<TMapper, TValue> { Name = address };
        if (arrayDimensions != null)
        {
            newTag.ArrayDimensions = arrayDimensions;
        }
        ConfigureTag(newTag);
        try
        {
            await newTag.InitializeAsync().ConfigureAwait(false);
            Status status = newTag.GetStatus();
            if (status != Status.Ok)
            {
                _logger?.LogWarning("初始化标签失败: {Address}, 状态: {Status}", address, status);
            }
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "初始化标签异常: {Address}", address);
            throw;
        }

        tagCache.TryAdd(cacheKey, newTag);
        return newTag;
    }

    private Tag<RealPlcMapper, float> GetFloatTag(string address)
        => GetTag<RealPlcMapper, float>($"float{address}", address);

    private Task<Tag<RealPlcMapper, float>> GetFloatTagAsync(string address)
        => GetTagAsync<RealPlcMapper, float>($"float{address}", address);

    private Tag<RealPlcMapper, float[]> GetFloatArrayTag(string address, int length)
        => GetTag<RealPlcMapper, float[]>($"float[]{address}_{length}", address, [1, length]);

    private Task<Tag<RealPlcMapper, float[]>> GetFloatArrayTagAsync(string address, int length)
        => GetTagAsync<RealPlcMapper, float[]>($"float[]{address}_{length}", address, [1, length]);

    private Tag<DintPlcMapper, int[]> GetDintArrayTag(string address, int length)
        => GetTag<DintPlcMapper, int[]>($"dint[]{address}_{length}", address, [1, length]);

    private Task<Tag<DintPlcMapper, int[]>> GetDintArrayTagAsync(string address, int length)
        => GetTagAsync<DintPlcMapper, int[]>($"dint[]{address}_{length}", address, [1, length]);

    private Tag<BoolPlcMapper, bool[]> GetBoolArrayTag(string address, int length)
        => GetTag<BoolPlcMapper, bool[]>($"bool[]{address}_{length}", address, [1, length]);

    private Task<Tag<BoolPlcMapper, bool[]>> GetBoolArrayTagAsync(string address, int length)
        => GetTagAsync<BoolPlcMapper, bool[]>($"bool[]{address}_{length}", address, [1, length]);

    private Tag<BoolPlcMapper, bool> GetBoolTag(string address)
        => GetTag<BoolPlcMapper, bool>($"bool{address}", address);

    private Task<Tag<BoolPlcMapper, bool>> GetBoolTagAsync(string address)
        => GetTagAsync<BoolPlcMapper, bool>($"bool{address}", address);

    private Tag<StringPlcMapper, string[]> GetStringArrayTag(string address, int length)
        => GetTag<StringPlcMapper, string[]>($"string[]{address}_{length}", address, [1, length]);

    private Task<Tag<StringPlcMapper, string[]>> GetStringArrayTagAsync(string address, int length)
        => GetTagAsync<StringPlcMapper, string[]>($"string[]{address}_{length}", address, [1, length]);

    private Tag<StringPlcMapper, string> GetStringTag(string address)
        => GetTag<StringPlcMapper, string>($"string{address}", address);

    private Task<Tag<StringPlcMapper, string>> GetStringTagAsync(string address)
        => GetTagAsync<StringPlcMapper, string>($"string{address}", address);

    private Tag GetBoolWordTag(string address)
    {
        string cacheKey = $"bool_word{address}";
        if (rawBoolTagCache.TryGetValue(cacheKey, out var existingTag))
        {
            return existingTag;
        }

        var tag = new Tag { Name = address };
        ConfigureTag(tag);
        try
        {
            tag.Initialize();
            Status status = tag.GetStatus();
            if (status != Status.Ok)
            {
                _logger?.LogWarning("初始化布尔标签失败: {Address}, 状态: {Status}", address, status);
            }
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "初始化布尔标签异常: {Address}", address);
        }

        rawBoolTagCache.TryAdd(cacheKey, tag);
        return tag;
    }

    private async Task<Tag> GetBoolWordTagAsync(string address)
    {
        string cacheKey = $"bool_word{address}";
        if (rawBoolTagCache.TryGetValue(cacheKey, out var existingTag))
        {
            return existingTag;
        }

        var tag = new Tag { Name = address };
        ConfigureTag(tag);
        try
        {
            await tag.InitializeAsync().ConfigureAwait(false);
            Status status = tag.GetStatus();
            if (status != Status.Ok)
            {
                _logger?.LogWarning("初始化布尔标签失败: {Address}, 状态: {Status}", address, status);
            }
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "初始化布尔标签异常: {Address}", address);
        }

        rawBoolTagCache.TryAdd(cacheKey, tag);
        return tag;
    }

    #endregion

    #region 同步方法实现

    /// <inheritdoc/>
    public float ReadFloat(string address)
    {
        try
        {

            var tag = GetFloatTag(address);
            tag.Read();
            return tag.Value;
        }
        catch (LibPlcTagException ex)
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

            var keyTagPair = GetFloatArrayTag(address, length);
            keyTagPair.Read();

            return new ArraySegment<float>(keyTagPair.Value, 0, Math.Min(length, keyTagPair.Value.Length));
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "读取浮点数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual void WriteFloat(string address, float writeValue)
    {
        try
        {

            var tag = GetFloatTag(address);
            tag.Value = writeValue;
            tag.Write();
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "写入浮点异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual void WriteFloatArray(string address, float[] writeValue)
    {
        try
        {

            var tag = GetFloatArrayTag(address, writeValue.Length);
            tag.Value = writeValue;
            tag.Write();
        }
        catch (LibPlcTagException ex)
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

            var tag = GetDintArrayTag(address, length);
            tag.Read();

            return new ArraySegment<int>(tag.Value, 0, Math.Min(length, tag.Value.Length));
        }
        catch (LibPlcTagException ex)
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

            var tag = GetDintArrayTag(address, writeValue.Length);
            tag.Value = writeValue;
            tag.Write();
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "写入DINT数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual ArraySegment<string> ReadStringArray(string address, int length)
    {
        try
        {

            var tag = GetStringArrayTag(address, length);
            tag.Read();

            return new ArraySegment<string>(tag.Value, 0, Math.Min(length, tag.Value.Length));
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "读取字符串数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual void WriteString(string address, string writeValue)
    {
        try
        {

            var tag = GetStringTag(address);
            tag.Value = writeValue;
            tag.Write();
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "写入字符串异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual void WriteStringArray(string address, string[] writeValue)
    {
        try
        {

            var tag = GetStringArrayTag(address, writeValue.Length);
            tag.Value = writeValue;
            tag.Write();
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "写入字符串数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public ArraySegment<bool> ReadBoolArray(string address, int requestedLength)
    {
        try
        {

            var tag = GetBoolArrayTag(address, requestedLength);
            tag.Read();

            return new ArraySegment<bool>(tag.Value, 0, Math.Min(requestedLength, tag.Value.Length));
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "读取布尔数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteBool(string address, bool writeValue)
    {
        try
        {

            if (PlcAddressHelper.TryParseBitAddress(address, out var baseAddress, out var bitIndex) && baseAddress != address)
            {
                var tag = GetBoolWordTag(baseAddress);
                tag.SetBit(bitIndex, writeValue);
                tag.Write();
            }
            else
            {
                var tag = GetBoolTag(address);
                tag.Value = writeValue;
                tag.Write();
            }
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "写入布尔值异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteBoolArray(string address, bool[] writeValue)
    {
        try
        {

            var tag = GetBoolArrayTag(address, writeValue.Length);
            tag.Value = writeValue;
            tag.Write();
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "写入布尔数组异常: {Address}", address);
            throw;
        }
    }
    #endregion

    #region 异步方法实现

    /// <inheritdoc/>
    public async Task<float> ReadFloatAsync(string address)
    {
        try
        {

            var tag = await GetFloatTagAsync(address).ConfigureAwait(false);
            await tag.ReadAsync().ConfigureAwait(false);
            return tag.Value;
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步读取浮点异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual async Task WriteFloatAsync(string address, float writeValue)
    {
        try
        {

            var tag = await GetFloatTagAsync(address).ConfigureAwait(false);
            tag.Value = writeValue;
            await tag.WriteAsync().ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步写入浮点异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ArraySegment<float>> ReadFloatArrayAsync(string address, int length)
    {
        try
        {

            var tag = await GetFloatArrayTagAsync(address, length).ConfigureAwait(false);
            await tag.ReadAsync().ConfigureAwait(false);

            return new ArraySegment<float>(tag.Value, 0, Math.Min(length, tag.Value.Length));
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步读取浮点数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual async Task WriteFloatArrayAsync(string address, float[] writeValue)
    {
        try
        {

            var tag = await GetFloatArrayTagAsync(address, writeValue.Length).ConfigureAwait(false);
            tag.Value = writeValue;
            await tag.WriteAsync().ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
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

            var tag = await GetDintArrayTagAsync(address, length).ConfigureAwait(false);
            await tag.ReadAsync().ConfigureAwait(false);

            return new ArraySegment<int>(tag.Value, 0, Math.Min(length, tag.Value.Length));
        }
        catch (LibPlcTagException ex)
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

            var tag = await GetDintArrayTagAsync(address, writeValue.Length).ConfigureAwait(false);
            tag.Value = writeValue;
            await tag.WriteAsync().ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步写入DINT数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual async Task<ArraySegment<string>> ReadStringArrayAsync(string address, int length)
    {
        try
        {

            var tag = await GetStringArrayTagAsync(address, length).ConfigureAwait(false);
            await tag.ReadAsync().ConfigureAwait(false);

            return new ArraySegment<string>(tag.Value, 0, Math.Min(length, tag.Value.Length));
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步读取字符串数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual async Task WriteStringAsync(string address, string writeValue)
    {
        try
        {

            var tag = await GetStringTagAsync(address).ConfigureAwait(false);
            tag.Value = writeValue;
            await tag.WriteAsync().ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步写入字符串异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public virtual async Task WriteStringArrayAsync(string address, string[] writeValue)
    {
        try
        {

            var tag = await GetStringArrayTagAsync(address, writeValue.Length).ConfigureAwait(false);
            tag.Value = writeValue;
            await tag.WriteAsync().ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步写入字符串数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ArraySegment<bool>> ReadBoolArrayAsync(string address, int requestedLength)
    {
        try
        {

            var tag = await GetBoolArrayTagAsync(address, requestedLength).ConfigureAwait(false);
            await tag.ReadAsync().ConfigureAwait(false);

            return new ArraySegment<bool>(tag.Value, 0, Math.Min(requestedLength, tag.Value.Length));
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步读取布尔数组异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteBoolAsync(string address, bool writeValue)
    {
        try
        {

            if (PlcAddressHelper.TryParseBitAddress(address, out var baseAddress, out var bitIndex) && baseAddress != address)
            {
                var tag = await GetBoolWordTagAsync(baseAddress).ConfigureAwait(false);
                tag.SetBit(bitIndex, writeValue);
                await tag.WriteAsync().ConfigureAwait(false);
            }
            else
            {
                var tag = await GetBoolTagAsync(address).ConfigureAwait(false);
                tag.Value = writeValue;
                await tag.WriteAsync().ConfigureAwait(false);
            }
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步写入布尔值异常: {Address}", address);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteBoolArrayAsync(string address, bool[] writeValue)
    {
        try
        {

            var tag = await GetBoolArrayTagAsync(address, writeValue.Length).ConfigureAwait(false);
            tag.Value = writeValue;
            await tag.WriteAsync().ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            _logger?.LogError(ex, "异步写入布尔数组异常: {Address}", address);
            throw;
        }
    }

    #endregion

    /// <inheritdoc/>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                CleanupTags();
            }

            _disposedValue = true;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
