using System;
using System.Threading.Tasks;

namespace PlcCipTag;

/// <summary>
/// PLC Tag 通信的统一接口，提供通过 CIP 协议基于 Tag 名称读写 PLC 数据的能力。
/// <para>
/// 支持的数据类型包括：float (REAL)、int (DINT)、bool (BOOL) 和 string (STRING)。
/// 每种数据类型均提供同步和异步两套方法。
/// </para>
/// <para>
/// 地址格式为 PLC Tag 名称，支持数组索引（如 <c>"myTag[0]"</c>）。
/// </para>
/// </summary>
public interface ITagPlc : IDisposable
{
    /// <summary>
    /// 释放所有缓存的 Tag 资源并重置内部连接状态。
    /// </summary>
    void CleanupTags();

    /// <summary>
    /// 从 PLC 读取单个浮点数 (REAL)。
    /// </summary>
    /// <param name="address">Tag 地址。</param>
    /// <returns>读取到的浮点值。</returns>
    float ReadFloat(string address);

    /// <inheritdoc cref="ReadFloat"/>
    Task<float> ReadFloatAsync(string address);

    /// <summary>
    /// 从 PLC 读取浮点数组 (REAL[])。超过单次读取上限时自动分块。
    /// </summary>
    /// <param name="address">Tag 地址，如 <c>"myTag"</c> 或 <c>"myTag[0]"</c>。</param>
    /// <param name="length">要读取的元素数量。</param>
    /// <returns>包含读取结果的 <see cref="ArraySegment{T}"/>。</returns>
    ArraySegment<float> ReadFloatArray(string address, int length);

    /// <inheritdoc cref="ReadFloatArray"/>
    Task<ArraySegment<float>> ReadFloatArrayAsync(string address, int length);

    /// <summary>
    /// 向 PLC 写入单个浮点数 (REAL)。
    /// </summary>
    /// <param name="address">Tag 地址。</param>
    /// <param name="value">要写入的浮点值。</param>
    void WriteFloat(string address, float value);

    /// <inheritdoc cref="WriteFloat"/>
    Task WriteFloatAsync(string address, float value);

    /// <summary>
    /// 向 PLC 写入浮点数组 (REAL[])。超过单次写入上限时自动分块。
    /// </summary>
    /// <param name="address">Tag 地址。</param>
    /// <param name="values">要写入的浮点数组。</param>
    void WriteFloatArray(string address, float[] values);

    /// <inheritdoc cref="WriteFloatArray"/>
    Task WriteFloatArrayAsync(string address, float[] values);

    /// <summary>
    /// 从 PLC 读取 DINT (32 位整数) 数组。超过单次读取上限时自动分块。
    /// </summary>
    /// <param name="address">Tag 地址。</param>
    /// <param name="length">要读取的元素数量。</param>
    /// <returns>包含读取结果的 <see cref="ArraySegment{T}"/>。</returns>
    ArraySegment<int> ReadDINTArray(string address, int length);

    /// <inheritdoc cref="ReadDINTArray"/>
    Task<ArraySegment<int>> ReadDINTArrayAsync(string address, int length);

    /// <summary>
    /// 向 PLC 写入 DINT (32 位整数) 数组。超过单次写入上限时自动分块。
    /// </summary>
    /// <param name="address">Tag 地址。</param>
    /// <param name="values">要写入的整数数组。</param>
    void WriteDINTArray(string address, int[] values);

    /// <inheritdoc cref="WriteDINTArray"/>
    Task WriteDINTArrayAsync(string address, int[] values);

    /// <summary>
    /// 从 PLC 读取布尔数组 (BOOL[])。
    /// 支持位访问前缀 <c>"i="</c>（仅 OMRON），可从 DINT/WORD 中按位提取。
    /// </summary>
    /// <param name="address">Tag 地址，如 <c>"myTag"</c> 或 <c>"i=myTag[3]"</c>（OMRON 位访问）。</param>
    /// <param name="length">要读取的布尔元素数量。</param>
    /// <returns>包含读取结果的 <see cref="ArraySegment{T}"/>。</returns>
    ArraySegment<bool> ReadBoolArray(string address, int length);

    /// <inheritdoc cref="ReadBoolArray"/>
    Task<ArraySegment<bool>> ReadBoolArrayAsync(string address, int length);

    /// <summary>
    /// 向 PLC 写入单个布尔值 (BOOL)。
    /// 支持位地址写入（如 <c>"myWord[7]"</c>），会自动执行 read-modify-write 操作。
    /// </summary>
    /// <param name="address">Tag 地址，可以是布尔 Tag 或位地址（如 <c>"myWord[7]"</c>）。</param>
    /// <param name="value">要写入的布尔值。</param>
    void WriteBool(string address, bool value);

    /// <inheritdoc cref="WriteBool"/>
    Task WriteBoolAsync(string address, bool value);

    /// <summary>
    /// 向 PLC 写入布尔数组 (BOOL[])。
    /// </summary>
    /// <param name="address">Tag 地址。</param>
    /// <param name="values">要写入的布尔数组。</param>
    void WriteBoolArray(string address, bool[] values);

    /// <inheritdoc cref="WriteBoolArray"/>
    Task WriteBoolArrayAsync(string address, bool[] values);

    /// <summary>
    /// 从 PLC 读取字符串数组 (STRING[])。
    /// 当 <paramref name="length"/> 为 1 且地址不含数组索引时，作为单个字符串读取。
    /// </summary>
    /// <param name="address">Tag 地址。</param>
    /// <param name="length">要读取的字符串数量。</param>
    /// <returns>包含读取结果的 <see cref="ArraySegment{T}"/>。</returns>
    ArraySegment<string> ReadStringArray(string address, int length);

    /// <inheritdoc cref="ReadStringArray"/>
    Task<ArraySegment<string>> ReadStringArrayAsync(string address, int length);

    /// <summary>
    /// 向 PLC 写入单个字符串 (STRING)。
    /// </summary>
    /// <param name="address">Tag 地址。</param>
    /// <param name="value">要写入的字符串值。</param>
    void WriteString(string address, string value);

    /// <inheritdoc cref="WriteString"/>
    Task WriteStringAsync(string address, string value);

    /// <summary>
    /// 向 PLC 写入字符串数组 (STRING[])。
    /// </summary>
    /// <param name="address">Tag 地址。</param>
    /// <param name="values">要写入的字符串数组。</param>
    void WriteStringArray(string address, string[] values);

    /// <inheritdoc cref="WriteStringArray"/>
    Task WriteStringArrayAsync(string address, string[] values);
}
