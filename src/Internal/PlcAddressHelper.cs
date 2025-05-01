using System;

namespace PlcCipTag;

/// <summary>
/// PLC Tag 地址解析工具类，提供数组索引提取和位地址解析等功能。
/// </summary>
public static class PlcAddressHelper
{
    /// <summary>
    /// 解析位地址，从 <c>"tagName[bitIndex]"</c> 格式中提取 Tag 名称和位索引。
    /// 用于布尔值写入时对 DINT/WORD 等整数类型执行 read-modify-write 操作。
    /// </summary>
    /// <param name="address">待解析的地址，如 <c>"myWord[7]"</c>。</param>
    /// <param name="tagName">输出解析得到的 Tag 名称（如 <c>"myWord"</c>）。解析失败时返回原始地址。</param>
    /// <param name="bitIndex">输出解析得到的位索引。解析失败时返回 0。</param>
    /// <returns>解析成功返回 <see langword="true"/>；地址不含方括号索引或格式不正确时返回 <see langword="false"/>。</returns>
    public static bool TryParseBitAddress(string address, out string tagName, out int bitIndex)
    {
        int openIndex = address.IndexOf('[');
        if (openIndex >= 0)
        {
            int closeIndex = address.IndexOf(']', openIndex + 1);
            if (closeIndex > openIndex + 1)
            {
                var numberPart = address.Substring(openIndex + 1, closeIndex - openIndex - 1);
                if (int.TryParse(numberPart, out bitIndex))
                {
                    tagName = address.Substring(0, openIndex);
                    return true;
                }
            }
        }

        tagName = address;
        bitIndex = 0;
        return false;
    }

    /// <summary>
    /// 解析数组地址中的起始索引，从 <c>"tagName[startIndex]"</c> 格式中提取基础名称和起始索引。
    /// 用于分块读写数组时计算每块的起始偏移。
    /// </summary>
    /// <param name="address">待解析的地址，如 <c>"myTag[10]"</c>。</param>
    /// <param name="baseName">输出解析得到的基础 Tag 名称（如 <c>"myTag"</c>）。解析失败时返回原始地址。</param>
    /// <param name="startIndex">输出解析得到的起始索引。解析失败时返回 0。</param>
    /// <returns>解析成功返回 <see langword="true"/>；地址不含数组索引时返回 <see langword="false"/>。</returns>
    public static bool TryParseArrayStartIndex(string address, out string baseName, out int startIndex)
    {
        int openIndex = address.IndexOf('[', 0);
        if (openIndex >= 0)
        {
            int closeIndex = address.IndexOf(']', openIndex + 1);
            if (closeIndex > openIndex + 1)
            {
                var numberPart = address.Substring(openIndex + 1, closeIndex - openIndex - 1);
                if (int.TryParse(numberPart, out startIndex))
                {
                    baseName = address.Substring(0, openIndex);
                    return true;
                }
            }
        }

        baseName = address;
        startIndex = 0;
        return false;
    }
}
