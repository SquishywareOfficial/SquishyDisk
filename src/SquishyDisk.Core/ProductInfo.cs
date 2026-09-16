using System.Reflection;

namespace SquishyDisk.Core;

public static class ProductInfo
{
    public const string Name = "SquishyDisk";
    public static string Version { get; } = typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public static string DisplayName => $"{Name} {Version}";
}
