using AssetRipper.IO.Endian;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AssetRipper.Import.Structure.Assembly.Serializable;

internal static class EndianSpanReaderExtensions
{
	public static T[] ReadPrimitiveArray<T>(this ref EndianSpanReader reader, UnityVersion version) where T : unmanaged
	{
		int count = reader.ReadInt32();
		count = FixArrayCount(ref reader, count);
		int index = 0;
		ThrowIfNegativeCount(count);
		ThrowIfNotEnoughSpaceForArray(ref reader, count, Unsafe.SizeOf<T>());
		T[] array = count == 0 ? [] : new T[count];
		while (index < count)
		{
			try
			{
				array[index] = reader.ReadPrimitive<T>();
			}
			catch (Exception ex)
			{
				throw new EndOfStreamException($"End of stream. Read {index}, expected {count} elements", ex);
			}
			index++;
		}
		if (IsAlignArrays(version))
		{
			reader.Align();
		}
		return array;
	}

	public static T[][] ReadPrimitiveArrayArray<T>(this ref EndianSpanReader reader, UnityVersion version) where T : unmanaged
	{
		int count = reader.ReadInt32();
		count = FixArrayCount(ref reader, count);
		int index = 0;
		ThrowIfNegativeCount(count);
		ThrowIfNotEnoughSpaceForArray(ref reader, count, sizeof(int));
		T[][] array = count == 0 ? [] : new T[count][];
		while (index < count)
		{
			try
			{
				array[index] = reader.ReadPrimitiveArray<T>(version);
			}
			catch (Exception ex)
			{
				throw new EndOfStreamException($"End of stream. Read {index}, expected {count} elements", ex);
			}
			index++;
		}
		if (IsAlignArrays(version))
		{
			reader.Align();
		}
		return array;
	}

	public static Utf8String ReadUtf8StringAligned(this ref EndianSpanReader reader)
	{
		Utf8String result = ReadUtf8StringSafe(ref reader);
		reader.Align();//Alignment after strings has happened since 2.1.0
		return result;
	}

	private static Utf8String ReadUtf8StringSafe(ref EndianSpanReader reader)
	{
		try
		{
			return reader.ReadUtf8String();
		}
		catch (ArgumentOutOfRangeException ex)
		{
			// 如果字符串长度读取失败，尝试修复
			long originalPosition = reader.Position;
			
			// 尝试读取字符串长度
			try
			{
				reader.Position = originalPosition;
				int length = reader.ReadInt32();
				
				// 检查长度是否合理
				if (length < 0 || length > 1000000)
				{
					// 尝试将长度解释为浮点数
					reader.Position = originalPosition;
					float floatLength = reader.ReadSingle();
					if (floatLength >= 0 && floatLength <= 1000000)
					{
						length = (int)Math.Round(floatLength);
					}
					else
					{
						// 如果仍然不合理，使用默认值
						length = 0;
					}
				}
				
				// 检查是否有足够的字节
				if (reader.Position + length > reader.Length)
				{
					length = (int)(reader.Length - reader.Position);
					if (length < 0) length = 0;
				}
				
				if (length > 0)
				{
					ReadOnlySpan<byte> bytes = reader.ReadBytesExact(length);
					return new Utf8String(bytes.ToArray());
				}
				else
				{
					return new Utf8String(Array.Empty<byte>());
				}
			}
			catch
			{
				// 如果所有修复尝试都失败，返回空字符串
				reader.Position = originalPosition;
				return new Utf8String(Array.Empty<byte>());
			}
		}
	}

	public static string[] ReadStringArray(this ref EndianSpanReader reader, UnityVersion version)
	{
		int count = reader.ReadInt32();
		count = FixArrayCount(ref reader, count);
		int index = 0;
		ThrowIfNegativeCount(count);
		ThrowIfNotEnoughSpaceForArray(ref reader, count, sizeof(int));
		string[] array = count == 0 ? [] : new string[count];
		while (index < count)
		{
			try
			{
				array[index] = reader.ReadUtf8StringAligned();
			}
			catch (Exception ex)
			{
				throw new EndOfStreamException($"End of stream. Read {index}, expected {count} elements", ex);
			}
			index++;
		}
		if (IsAlignArrays(version))
		{
			reader.Align();
		}
		return array;
	}

	public static string[][] ReadStringArrayArray(this ref EndianSpanReader reader, UnityVersion version)
	{
		int count = reader.ReadInt32();
		count = FixArrayCount(ref reader, count);
		int index = 0;
		ThrowIfNegativeCount(count);
		ThrowIfNotEnoughSpaceForArray(ref reader, count, sizeof(int));
		string[][] array = count == 0 ? [] : new string[count][];
		while (index < count)
		{
			try
			{
				array[index] = reader.ReadStringArray(version);
			}
			catch (Exception ex)
			{
				throw new EndOfStreamException($"End of stream. Read {index}, expected {count} elements", ex);
			}
			index++;
		}
		if (IsAlignArrays(version))
		{
			reader.Align();
		}
		return array;
	}

	private static bool IsAlignArrays(UnityVersion version) => version.GreaterThanOrEquals(2017);

	private static int FixArrayCount(ref EndianSpanReader reader, int originalCount)
	{
		// 检查count是否合理，如果异常大可能是类型混淆
		if (originalCount < 0 || originalCount > 1000000)
		{
			// 保存当前位置
			int originalPosition = (int)reader.Position;
			
			// 尝试回退并重新读取，可能是浮点数被误读为整数
			reader.Position -= 4; // 回退4字节
			float floatValue = reader.ReadSingle();
			
			// 如果浮点数值合理（接近整数），使用它作为count
			if (floatValue >= 0 && floatValue <= 1000000 && Math.Abs(floatValue - Math.Round(floatValue)) < 0.001f)
			{
				int count = (int)Math.Round(floatValue);
				// 重要：将reader位置恢复到原始位置，因为后续代码期望的是整数count的位置
				reader.Position = originalPosition;
				return count;
			}
			else
			{
				// 如果仍然不合理，尝试其他修复策略
				reader.Position = originalPosition - 4; // 回到原始位置前4字节
				
				// 尝试读取为更小的数据类型
				if (reader.Position + 2 <= reader.Length)
				{
					short shortCount = reader.ReadInt16();
					if (shortCount >= 0 && shortCount <= 10000)
					{
						// 恢复位置到原始位置
						reader.Position = originalPosition;
						return shortCount;
					}
					else
					{
						// 最后的修复尝试：基于剩余字节数估算合理的count
						reader.Position = originalPosition; // 恢复到原始位置
						long remainingBytes = reader.Length - reader.Position;
						if (remainingBytes > 0)
						{
							// 假设每个元素至少4字节，估算最大可能的count
							int estimatedCount = Math.Min((int)(remainingBytes / 4), 1000);
							if (estimatedCount > 0)
							{
								return estimatedCount;
							}
							else
							{
								throw new InvalidDataException($"Cannot determine valid array count. Original value: {originalCount}, Remaining bytes: {remainingBytes}");
							}
						}
						else
						{
							throw new InvalidDataException($"Cannot determine valid array count. Original value: {originalCount}");
						}
					}
				}
				else
				{
					throw new InvalidDataException($"Cannot determine valid array count. Original value: {originalCount}");
				}
			}
		}
		
		return originalCount;
	}

	[DebuggerHidden]
	private static void ThrowIfNegativeCount(int count)
	{
		if (count < 0)
		{
			throw new InvalidDataException($"Count cannot be negative: {count}");
		}
	}

	[DebuggerHidden]
	private static void ThrowIfNotEnoughSpaceForArray(ref EndianSpanReader reader, int elementNumberToRead, int elementSize)
	{
		int remainingBytes = reader.Length - reader.Position;
		if (remainingBytes < (long)elementNumberToRead * elementSize)
		{
			throw new EndOfStreamException($"Stream only has {remainingBytes} bytes in the stream, so {elementNumberToRead} elements of size {elementSize} cannot be read.");
		}
	}
}
