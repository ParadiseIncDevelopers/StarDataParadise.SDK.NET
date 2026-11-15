using System;
using StarDataParadise.SDK;

var version = SdkVersion.AssemblyVersion;
Console.WriteLine(version);
return version == new Version(0, 1, 0, 0) ? 0 : 1;
