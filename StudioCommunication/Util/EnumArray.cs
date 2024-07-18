using System;

namespace StudioCommunication.Util;

/// Type-safe wrapper around an array using enum values as indices
public readonly struct EnumArray<TEnum, TValue> where TEnum : struct, Enum {
    private readonly TValue[] data;
    
    public EnumArray() {
        var values = Enum.GetValuesAsUnderlyingType<TEnum>();
        
        long maxIdx = 0;
        foreach (var value in values) {
            maxIdx = Math.Max(maxIdx, Convert.ToInt64(value));
        }
        data = new TValue[maxIdx + 1];
    }
    
    public TValue this[TEnum index] {
        get => data[Convert.ToInt64(index)]; 
        set => data[Convert.ToInt64(index)] = value;
    }
}