# .NET SDK derleme ve test

.NET SDK 10.0.402 ve `net10.0` kullanılır. Çalışma zamanı ve geliştirme araçları için [dotnet/runtime lisansı](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT). SDK ek NuGet bağımlılığı taşımaz.

```powershell
powershell -NoProfile -File .\ci.ps1
```

Komut `out/packages/StarDataParadise.SDK.NET.0.1.0.nupkg` üretir. Bağımsız smoke uygulaması yerel paket deposundaki tam sürüme bağlanır ve yüklenen assembly sürümünü kontrol eder.
