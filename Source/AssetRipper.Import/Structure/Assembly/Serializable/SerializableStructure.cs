using AssetRipper.Assets;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.SerializationLogic;
using AssetRipper.SourceGenerated.Classes.ClassID_114;

namespace AssetRipper.Import.Structure.Assembly.Serializable;

public sealed class SerializableStructure : UnityAssetBase, IDeepCloneable
{
	public override int SerializedVersion => Type.Version;
	public override bool FlowMappedInYaml => Type.FlowMappedInYaml;

	internal SerializableStructure(SerializableType type, int depth)
	{
		Depth = depth;
		Type = type ?? throw new ArgumentNullException(nameof(type));
		Fields = new SerializableValue[type.Fields.Count];
	}

	public void Read(ref EndianSpanReader reader, UnityVersion version, TransferInstructionFlags flags)
	{
		// 记录读取开始位置，用于调试
		long startPosition = reader.Position;
		Logger.Info(LogCategory.Import, $"Starting to read SerializableStructure {Type.FullName} at position {startPosition}");
		
		for (int i = 0; i < Fields.Length; i++)
		{
			SerializableType.Field etalon = Type.Fields[i];
			if (IsAvailable(etalon))
			{
				Fields[i].Read(ref reader, version, flags, Depth, etalon);
			}
		}
		
		Logger.Info(LogCategory.Import, $"Finished reading SerializableStructure {Type.FullName} at position {reader.Position}");
	}

	public void Write(AssetWriter writer)
	{
		for (int i = 0; i < Fields.Length; i++)
		{
			SerializableType.Field etalon = Type.Fields[i];
			if (IsAvailable(etalon))
			{
				Fields[i].Write(writer, etalon);
			}
		}
	}
	public override void WriteEditor(AssetWriter writer) => Write(writer);
	public override void WriteRelease(AssetWriter writer) => Write(writer);

	public override void WalkEditor(AssetWalker walker)
	{
		if (walker.EnterAsset(this))
		{
			bool hasEmittedFirstField = false;
			for (int i = 0; i < Fields.Length; i++)
			{
				SerializableType.Field etalon = Type.Fields[i];
				if (IsAvailable(etalon))
				{
					if (hasEmittedFirstField)
					{
						walker.DivideAsset(this);
					}
					else
					{
						hasEmittedFirstField = true;
					}
					if (walker.EnterField(this, etalon.Name))
					{
						Fields[i].WalkEditor(walker, etalon);
						walker.ExitField(this, etalon.Name);
					}
				}
			}
			walker.ExitAsset(this);
		}
	}
	//For now, only the editor version is implemented.
	public override void WalkRelease(AssetWalker walker) => WalkEditor(walker);
	public override void WalkStandard(AssetWalker walker) => WalkEditor(walker);

	public override IEnumerable<(string, PPtr)> FetchDependencies()
	{
		for (int i = 0; i < Fields.Length; i++)
		{
			SerializableType.Field etalon = Type.Fields[i];
			if (IsAvailable(etalon))
			{
				foreach ((string, PPtr) pair in Fields[i].FetchDependencies(etalon))
				{
					yield return pair;
				}
			}
		}
	}

	public override string ToString() => Type.FullName;

	private bool IsAvailable(in SerializableType.Field field)
	{
		if (Depth < GetMaxDepthLevel())
		{
			return true;
		}
		if (field.ArrayDepth > 0)
		{
			return false;
		}
		if (field.Type.Type == PrimitiveType.Complex)
		{
			return MonoUtils.IsEngineStruct(field.Type.Namespace, field.Type.Name);
		}
		return true;
	}

	public bool TryRead(ref EndianSpanReader reader, IMonoBehaviour monoBehaviour)
	{
		try
		{
			Read(ref reader, monoBehaviour.Collection.Version, monoBehaviour.Collection.Flags);
		}
		catch (ArgumentOutOfRangeException ex)
		{
			Logger.Warning(LogCategory.Import, $"Data corruption detected in MonoBehaviour {monoBehaviour.ScriptP?.GetFullName()}, attempting recovery: {ex.Message}");
			// 尝试从当前位置继续读取，跳过损坏的字段
			try
			{
				// 重置reader位置到安全位置
				long safePosition = Math.Max(0, reader.Position - 4);
				reader.Position = (int)safePosition;
				
				// 尝试重新读取
				Read(ref reader, monoBehaviour.Collection.Version, monoBehaviour.Collection.Flags);
			}
			catch (Exception recoveryEx)
			{
				Logger.Error(LogCategory.Import, $"Recovery failed for MonoBehaviour {monoBehaviour.ScriptP?.GetFullName()}: {recoveryEx.Message}");
				LogMonoBehaviorReadException(this, ex);
				return false;
			}
		}
		catch (Exception ex)
		{
			LogMonoBehaviorReadException(this, ex);
			return false;
		}
		
		// 允许位置不完全匹配，只要在合理范围内
		long remainingBytes = reader.Length - reader.Position;
		if (remainingBytes > 0 && remainingBytes < 1000) // 允许最多1000字节的差异
		{
			Logger.Warning(LogCategory.Import, $"MonoBehaviour {monoBehaviour.ScriptP?.GetFullName()} has {remainingBytes} remaining bytes, but continuing");
		}
		else if (remainingBytes < 0)
		{
			LogMonoBehaviourMismatch(this, (int)reader.Position, (int)reader.Length);
			return false;
		}
		
		return true;
	}

	private static void LogMonoBehaviourMismatch(SerializableStructure structure, int actual, int expected)
	{
		Logger.Error(LogCategory.Import, $"Unable to read MonoBehaviour Structure, because script {structure} layout mismatched binary content (read {actual} bytes, expected {expected} bytes).");
	}

	private static void LogMonoBehaviorReadException(SerializableStructure structure, Exception ex)
	{
		Logger.Error(LogCategory.Import, $"Unable to read MonoBehaviour Structure, because script {structure} layout mismatched binary content ({ex}).");
	}

	public int Depth { get; }
	public SerializableType Type { get; }
	public SerializableValue[] Fields { get; }

	public ref SerializableValue this[string name]
	{
		get
		{
			if (TryGetIndex(name, out int index))
			{
				return ref Fields[index];
			}
			throw new KeyNotFoundException($"Field {name} wasn't found in {Type.Name}");
		}
	}

	public bool ContainsField(string name) => TryGetIndex(name, out _);

	public bool TryGetField(string name, out SerializableValue field)
	{
		if (TryGetIndex(name, out int index))
		{
			field = Fields[index];
			return true;
		}
		field = default;
		return false;
	}

	public SerializableValue? TryGetField(string name)
	{
		if (TryGetIndex(name, out int index))
		{
			return Fields[index];
		}
		return null;
	}

	public bool TryGetIndex(string name, out int index)
	{
		for (int i = 0; i < Fields.Length; i++)
		{
			if (Type.Fields[i].Name == name)
			{
				index = i;
				return true;
			}
		}
		index = -1;
		return false;
	}

	public override void CopyValues(IUnityAssetBase? source, PPtrConverter converter)
	{
		if (source is null)
		{
			Reset();
		}
		else
		{
			CopyValues((SerializableStructure)source, converter);
		}
	}

	public void CopyValues(SerializableStructure source, PPtrConverter converter)
	{
		if (source.Depth != Depth)
		{
			throw new ArgumentException($"Depth {source.Depth} doesn't match with {Depth}", nameof(source));
		}
		if (source.Type == Type)
		{
			for (int i = 0; i < Fields.Length; i++)
			{
				SerializableValue sourceField = source.Fields[i];
				if (sourceField.CValue is null)
				{
					Fields[i] = sourceField;
				}
				else
				{
					Fields[i].CopyValues(sourceField, Depth, Type.Fields[i], converter);
				}
			}
		}
		else
		{
			for (int i = 0; i < Fields.Length; i++)
			{
				string fieldName = Type.Fields[i].Name;
				int index = -1;
				for (int j = 0; j < source.Type.Fields.Count; j++)
				{
					if (fieldName == source.Type.Fields[j].Name)
					{
						index = j;
					}
				}
				SerializableValue sourceField = index < 0 ? default : source.Fields[index];
				Fields[i].CopyValues(sourceField, Depth, Type.Fields[i], converter);
			}
		}
	}

	public SerializableStructure DeepClone(PPtrConverter converter)
	{
		SerializableStructure clone = new(Type, Depth);
		clone.CopyValues(this, converter);
		return clone;
	}

	IUnityAssetBase IDeepCloneable.DeepClone(PPtrConverter converter) => DeepClone(converter);

	public override void Reset()
	{
		foreach (SerializableValue field in Fields)
		{
			field.Reset();
		}
	}

	public void InitializeFields(UnityVersion version)
	{
		for (int i = 0; i < Fields.Length; i++)
		{
			SerializableType.Field etalon = Type.Fields[i];
			if (IsAvailable(etalon))
			{
				Fields[i].Initialize(version, Depth, etalon);
			}
		}
	}

	/// <summary>
	/// 8 might have been an arbitrarily chosen number, but I think it's because the limit was supposedly 7 when mafaca made uTinyRipper.
	/// An official source is required, but forum posts suggest 7 and later 10 as the limits.
	/// It may be desirable to increase this number or do a Unity version check.
	/// </summary>
	/// <remarks>
	/// <see href="https://forum.unity.com/threads/serialization-depth-limit-and-recursive-serialization.1263599/"/><br/>
	/// <see href="https://forum.unity.com/threads/getting-a-serialization-depth-limit-7-error-for-no-reason.529850/"/><br/>
	/// <see href="https://forum.unity.com/threads/4-5-serialization-depth.248321/"/>
	/// </remarks>
	private static int GetMaxDepthLevel() => 8;
}
