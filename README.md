# StarDataParadise ADO.NET SDK

StarDataParadise Server’a wire v1 üzerinden bağlanan ADO.NET sağlayıcıları. `SdpConnection` ve `SdpCommand` ile parametreli SQL, transaction, savepoint ve yordam sonuçları kullanılabilir. Sağlayıcılar Native DLL gerektirmez.

| Paket | Hedef | Sürüm |
| --- | --- | --- |
| `StarDataParadiseDrive` | .NET 8 ve .NET 10 | `2.1.0` |
| `StarDataParadiseNetFramework` | .NET Framework 4.8 | `2.0.0` |

## Paketi uygulamaya ekleme

.NET 8 veya .NET 10 uygulama projesinde `2.1.0` paketini içeren NuGet kaynağını kullanın. Depodaki `ci.ps1` paketi `out/packages` altında üretir. Örnekteki `C:\SDP\packages` yolunu paketi içeren dizinle değiştirin.

```powershell
dotnet add package StarDataParadiseDrive --version 2.1.0 --source C:\SDP\packages
```

.NET Framework 4.8 için yapılandırdığınız NuGet kaynağından şu paketi ekleyin. Eski `1.x` node API’sinden geçiş yapıyorsanız `2.x` örneklerindeki ADO.NET tiplerini kullanın. [Framework 4.8 örneği ve sağlayıcı kaydı](framework/BUILD.md)

```xml
<PackageReference Include="StarDataParadiseNetFramework" Version="2.0.0" />
```

## TLS bağlantısı ve ilk sorgu

Örnek .NET 8/10 içindir. `items` tablosunda `id` ile `note` sütunları ve bunları okuma yetkisi gerekir. `SDP_USER`, `SDP_PASSWORD` ve `SDP_CA` işlem ortamı değişkenlerini sağlayın. `SDP_CA`, Server sertifikasını doğrulayan PEM dosyasıdır.

```csharp
using StarDataParadise.SDK;

var settings = SdpFactory.Instance.CreateConnectionStringBuilder();
settings["Host"] = "127.0.0.1";
settings["Port"] = 7443;
settings["TLS Name"] = "localhost";
settings["CA File"] = Environment.GetEnvironmentVariable("SDP_CA") ?? throw new InvalidOperationException("SDP_CA");
settings["User"] = Environment.GetEnvironmentVariable("SDP_USER") ?? throw new InvalidOperationException("SDP_USER");
settings["Password"] = Environment.GetEnvironmentVariable("SDP_PASSWORD") ?? throw new InvalidOperationException("SDP_PASSWORD");

await using var connection = new SdpConnection(settings.ConnectionString);
await connection.OpenAsync();
await using var command = connection.CreateCommand();
command.CommandText = "SELECT note FROM items WHERE id = :key";
command.Parameters.Add(new SdpParameter("key", 7L));
await using var rows = await command.ExecuteReaderAsync();
while (await rows.ReadAsync())
    Console.WriteLine(rows.GetValue(0));
```

TLS varsayılan bağlantı kipidir. Çağıranın sistem güven deposu kullanılabilir veya `CA File` ile güvenilen PEM belirtilebilir. Yerel geliştirmede, ortak dinleyici açılmışsa, Drive 2.1.0 `Host=127.0.0.1;Port=3197;TLS Mode=Disabled` alanlarıyla bağlanabilir. Bu kip loopback ile sınırlıdır ve kimlik bilgileri şifresiz iletilir. [MySQL tabanlı wire v1 dinleyicisi](https://github.com/ParadiseIncDevelopers/StarDataParadiseServer/tree/master/compatibility/wire_v1)

## İşlemler ve derleme

- Açık bir transaction içindeki komutları ilgili `SdpTransaction` ile ilişkilendirin. Bir sonraki komuttan önce okuyucuyu kapatın. Tipli NULL için `DbType` veya `SqlTypeName` kullanın.
- Bir yazma veya commit yanıtı kaybolduğunda `SdpException.UnknownOutcome` ve `RequestToken` alanlarını kontrol edin. Aynı kimlikle yeniden bağlanıp `GetOutcomeAsync` ile sonucu sorgulayın.
- Sunucu yeteneklerini `GetCapabilitiesAsync` ile kontrol edin. SQL, yönetim ve model çağrıları sunucunun özelliklerine ve oturum izinlerine bağlıdır.

Modern paketi .NET SDK 10.0.402 ile derleyip kurulu paket tüketicilerini çalıştırın. Tüketici testleri için .NET 8 ve .NET 10 çalışma zamanları gerekir.

```powershell
pwsh -NoProfile -File .\ci.ps1
```

[Modern ADO.NET kılavuzu ve canlı TLS testi](BUILD.md) · [Kaynak lisansı](LICENSE) · [Framework 4.8 kılavuzu](framework/BUILD.md) · [Server ve diğer SDK’lar](https://github.com/ParadiseIncDevelopers/StarDataParadiseServer)

## Native AI ve SQL oturumu

Server 0.5.0 ve `native_sql_v1` desteği için `await SdpNativeConnection.OpenAsync(connectionString)` kullanın. Ayrı oturum `?` parametrelerini native prepared protocol ile gönderir; SQL metni ve yordam gövdeleri korunur. Kesilen yanıtlarda otomatik yazma tekrarı yapılmaz. AI turn commit sonucu ile araç işleminin rollback sonucu ayrı hata alanlarında saklanır. [Native API ve sınır testleri](BUILD.md#native-sql-sessions).

## Lisans

Bu depodaki modern .NET ve .NET Framework ADO.NET sağlayıcıları mevcut
[MIT Lisansı](LICENSE) altında sunulur. Telif ve izin bildirimi korunarak
ticari kullanım, değiştirme ve yeniden dağıtıma izin verilir.

İki NuGet hedefi de MIT lisansını paketleme ayarlarında belirtir ve LICENSE
dosyasını içerir. Bağımlılık ve ürün kapsamı: [LICENSING.md](LICENSING.md).
