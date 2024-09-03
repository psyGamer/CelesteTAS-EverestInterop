using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Celeste.Mod;

namespace TAS.Utils;

internal static class HashHelper {
    private static MethodInfo m_Create;
    
    public static string ComputeHash(string text) {
        m_Create ??= typeof(Everest).Assembly.GetType("Celeste.Mod.XXHash64")?.GetMethodInfo("Create", new Type[]{}, BindingFlags.Public | BindingFlags.Static); 
        m_Create ??= typeof(Everest).Assembly.GetType("YYProject.XXHash.XXHash64")?.GetMethodInfo("Create", new Type[]{}, BindingFlags.Public | BindingFlags.Static);
        
        HashAlgorithm algo = (HashAlgorithm)m_Create!.Invoke(null, new object[]{});
        return algo.ComputeHash(Encoding.UTF8.GetBytes(text)).ToHexadecimalString();
    }
}