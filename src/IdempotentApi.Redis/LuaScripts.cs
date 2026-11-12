namespace IdempotentApi.Redis;

/// <summary>The Lua sources, embedded so the package ships them and tests read exactly what production runs.</summary>
internal static class LuaScripts
{
    public static readonly string Begin = Load("begin");
    public static readonly string Complete = Load("complete");
    public static readonly string Renew = Load("renew");
    public static readonly string Release = Load("release");
    public static readonly string TokenBucket = Load("token_bucket");

    private static string Load(string name)
    {
        using var stream = typeof(LuaScripts).Assembly.GetManifestResourceStream($"IdempotentApi.Redis.Scripts.{name}.lua")
            ?? throw new InvalidOperationException($"Embedded script {name}.lua is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
