using System.Reflection;

namespace NotedX.MobileWeb;

/// <summary>Identifies the assembly that embeds the mobile PWA assets.</summary>
public static class MobileWebAssets
{
    /// <summary>Logical root of the PWA files inside the assembly manifest.</summary>
    public const string RootPath = "wwwroot";

    /// <summary>Assembly containing the embedded <c>wwwroot</c> files.</summary>
    public static Assembly Assembly => typeof(MobileWebAssets).Assembly;
}
