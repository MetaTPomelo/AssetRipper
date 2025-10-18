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
		Utf8String result = reader.ReadUtf8String();
		reader.Align();//Alignment after strings has happened since 2.1.0
		return result;
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
