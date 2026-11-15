using System;
using System.Reflection;

namespace StarDataParadise.SDK;

public static class SdkVersion
{
    public static Version AssemblyVersion =>
        typeof(SdkVersion).Assembly.GetName().Version!;
}
