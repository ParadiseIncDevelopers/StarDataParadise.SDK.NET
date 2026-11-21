using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StarDataParadise.SDK;

internal sealed class SdpWire : IDisposable
{
    private const int ProtocolVersion = 1;
    private const int MaxFrame = 1024 * 1024;
#if NET8_0
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 32 };
#else
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 32, AllowDuplicateProperties = false };
#endif
    private static readonly JsonSerializerOptions SerializerOptions = new() { MaxDepth = 32 };
    private static readonly object EmptyBody = new { };

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly TimeSpan _timeout;
    private readonly X509Certificate2Collection _roots;
    private readonly SemaphoreSlim _singleExchange = new(1, 1);
    private uint _lastRequestId;
    private uint _pendingRequestId;
    private int _disposed;

    private SdpWire(TcpClient client, Stream stream, TimeSpan timeout, X509Certificate2Collection roots)
    {
        _client = client;
        _stream = stream;
        _timeout = timeout;
        _roots = roots;
    }

    internal string SessionId { get; private set; } = "";
    internal string ProductVersion { get; private set; } = "";
    internal bool IsOpen => Volatile.Read(ref _disposed) == 0;
    internal uint CurrentRequestId => Volatile.Read(ref _pendingRequestId);

    internal static async Task<SdpWire> OpenAsync(string host, int port, string tlsName, string? caFile,
        IPAddress? plaintextAddress, string user, string password, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var client = plaintextAddress is null ? new TcpClient() : new TcpClient(plaintextAddress.AddressFamily);
        Stream? stream = null;
        var roots = new X509Certificate2Collection();
        var opened = false;
        try
        {
            client.NoDelay = true;
            if (!string.IsNullOrEmpty(caFile)) ImportRoots(roots, caFile!);
#if NETFRAMEWORK
            using var cancellation = deadline.Token.Register(client.Close);
            if (plaintextAddress is null)
                await client.ConnectAsync(host, port).ConfigureAwait(false);
            else
                await client.ConnectAsync(plaintextAddress, port).ConfigureAwait(false);
#else
            if (plaintextAddress is null)
                await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
            else
                await client.ConnectAsync(plaintextAddress, port, deadline.Token).ConfigureAwait(false);
#endif
            stream = client.GetStream();
            if (plaintextAddress is null)
            {
                var target = string.IsNullOrEmpty(tlsName) ? host : tlsName;
#if NETFRAMEWORK
                var tls = new SslStream(stream, false,
                    (_, certificate, _, errors) => ValidateFrameworkCertificate(roots, certificate, errors));
                stream = tls;
                await tls.AuthenticateAsClientAsync(target, new X509CertificateCollection(), SslProtocols.Tls12, false)
                    .ConfigureAwait(false);
#else
                var tls = new SslStream(stream);
                stream = tls;
                var authentication = new SslClientAuthenticationOptions
                {
                    TargetHost = target,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowRenegotiation = false,
                };
                if (roots.Count != 0)
                {
                    authentication.CertificateChainPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        RevocationMode = X509RevocationMode.NoCheck,
                    };
                    authentication.CertificateChainPolicy.CustomTrustStore.AddRange(roots);
                }
                await tls.AuthenticateAsClientAsync(authentication, deadline.Token).ConfigureAwait(false);
#endif
            }
            deadline.Token.ThrowIfCancellationRequested();
            var wire = new SdpWire(client, stream, timeout, roots);
            var hello = await wire.CallAsync("hello", null, deadline.Token,
                allowClassicGreeting: plaintextAddress is not null).ConfigureAwait(false);
            if (hello.GetProperty("protocol_version").GetInt32() != ProtocolVersion)
                throw new SdpException(1003, "0A000", "Incompatible protocol version");
            wire.SessionId = hello.GetProperty("session_id").GetString() ?? throw new JsonException("Missing session ID");
            wire.ProductVersion = hello.GetProperty("product_version").GetString() ?? throw new JsonException("Missing server version");
            var auth = await wire.CallAsync("auth", new { username = user, password }, deadline.Token).ConfigureAwait(false);
            if (!auth.GetProperty("authenticated").GetBoolean())
                throw new SdpException(1005, "28000", "Authentication failed");
            opened = true;
            return wire;
        }
        catch (OperationCanceledException e)
        {
            throw Interrupted(cancellationToken, null, e);
        }
        catch (SdpException e) when (e.Code == 1009 && !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw Interrupted(cancellationToken, null, e);
        }
        catch (AuthenticationException e)
        {
            throw new SdpException(1011, "08001", "TLS connection failed", inner: e);
        }
        catch (CryptographicException e)
        {
            throw new SdpException(1011, "08001", "TLS trust file could not be loaded", inner: e);
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            if (deadline.IsCancellationRequested) throw Interrupted(cancellationToken, null, e);
            throw new SdpException(1010, "08006", "Connection could not be opened", inner: e);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            throw new SdpException(1001, "08P01", "Malformed protocol handshake", inner: e);
        }
        finally
        {
            if (!opened)
            {
                client.Close();
                stream?.Dispose();
                foreach (var root in roots) root.Dispose();
            }
        }
    }

    internal async Task<JsonElement> CallAsync(string operation, object? body, CancellationToken cancellationToken,
        string? requestToken = null, bool allowClassicGreeting = false, bool uncertainOnSend = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var locked = false;
        var sent = false;
        byte[]? payload = null;
        try
        {
            await _singleExchange.WaitAsync(deadline.Token).ConfigureAwait(false);
            locked = true;
            if (!IsOpen)
                throw new SdpException(1010, "08006", "Connection closed");
            if (_lastRequestId == uint.MaxValue)
            {
                Dispose();
                throw new SdpException(1013, "55000", "Connection request counter is exhausted");
            }

            var id = ++_lastRequestId;
            payload = JsonSerializer.SerializeToUtf8Bytes(new { v = ProtocolVersion, id, op = operation, body = body ?? EmptyBody },
                SerializerOptions);
            if (payload.Length > MaxFrame)
                throw new SdpException(1002, "54000", "Protocol frame exceeds the size limit");

            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
            deadline.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref _pendingRequestId, id);
            sent = true;
#if NETFRAMEWORK
            using var interrupt = deadline.Token.Register(_client.Close);
#endif
            await _stream.WriteAsync(header, 0, header.Length, deadline.Token).ConfigureAwait(false);
            await _stream.WriteAsync(payload, 0, payload.Length, deadline.Token).ConfigureAwait(false);
            await ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
            if (allowClassicGreeting && header[0] >= 2 && header[1] == 0 && header[2] == 0 && header[3] == 0)
            {
                // The local shared listener sends a Classic greeting for server-first clients.
                // Our wire hello was already sent. Consume the greeting before reading its reply.
                var greeting = new byte[header[0]];
                await ReadExactlyAsync(greeting, deadline.Token).ConfigureAwait(false);
                var versionEnd = Array.IndexOf(greeting, (byte)0, 1);
                if (greeting[0] != 10 || versionEnd <= 1 ||
                    !Encoding.ASCII.GetString(greeting, 1, versionEnd - 1)
                        .EndsWith("-StarDataParadise", StringComparison.Ordinal))
                    throw new JsonException("Invalid StarDataParadise Classic greeting");
                await ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
            }
            var size = BinaryPrimitives.ReadInt32BigEndian(header);
            if (size is <= 0 or > MaxFrame) throw new JsonException("Invalid protocol frame length");
            var incoming = new byte[size];
            await ReadExactlyAsync(incoming, deadline.Token).ConfigureAwait(false);
            using var response = JsonDocument.Parse(incoming, DocumentOptions);
            var packet = response.RootElement;
#if NET8_0
            RejectDuplicateProperties(packet);
#endif
            if (packet.GetProperty("v").GetInt32() != ProtocolVersion || packet.GetProperty("id").GetUInt32() != id)
                throw new JsonException("Uncorrelated response");
            if (!packet.GetProperty("ok").GetBoolean())
                throw SdpException.FromWire(packet.GetProperty("error"), requestToken);
            var result = packet.GetProperty("body");
            if (result.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a protocol body object");
            return result.Clone();
        }
        catch (OperationCanceledException e)
        {
            if (sent) Dispose();
            throw Interrupted(cancellationToken, sent ? requestToken : null, e, sent && uncertainOnSend);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            if (sent) Dispose();
            if (deadline.IsCancellationRequested)
                throw Interrupted(cancellationToken, sent ? requestToken : null, e, sent && uncertainOnSend);
            throw new SdpException(1010, "08006", "Connection closed during the exchange",
                sent && (requestToken is not null || uncertainOnSend), sent ? requestToken : null, inner: e);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException
            or System.Collections.Generic.KeyNotFoundException)
        {
            if (sent) Dispose();
            throw new SdpException(1001, "08P01", "Malformed protocol message",
                sent && (requestToken is not null || uncertainOnSend), sent ? requestToken : null, inner: e);
        }
        finally
        {
            if (payload is not null) Array.Clear(payload, 0, payload.Length);
            if (locked)
            {
                Volatile.Write(ref _pendingRequestId, 0);
                _singleExchange.Release();
            }
        }
    }

#if NET8_0
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException("Duplicate JSON property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
#endif

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await _stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Truncated Server frame");
            offset += count;
        }
    }

    private static void ImportRoots(X509Certificate2Collection roots, string path)
    {
#if NETFRAMEWORK
        const string start = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        var pem = File.ReadAllText(path);
        var position = 0;
        while ((position = pem.IndexOf(start, position, StringComparison.Ordinal)) >= 0)
        {
            var body = position + start.Length;
            var endPosition = pem.IndexOf(end, body, StringComparison.Ordinal);
            if (endPosition < 0) throw new AuthenticationException("Incomplete TLS certificate");
            roots.Add(new X509Certificate2(Convert.FromBase64String(pem.Substring(body, endPosition - body))));
            position = endPosition + end.Length;
        }
#else
        roots.ImportFromPemFile(path);
#endif
        if (roots.Count == 0) throw new AuthenticationException("Empty TLS trust file");
    }

#if NETFRAMEWORK
    private static bool ValidateFrameworkCertificate(X509Certificate2Collection roots, X509Certificate? certificate,
        SslPolicyErrors errors)
    {
        if (roots.Count == 0) return errors == SslPolicyErrors.None;
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;

        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (!chain.Build(leaf) || chain.ChainElements.Count == 0) return false;
        foreach (var status in chain.ChainStatus)
            if ((status.Status & ~X509ChainStatusFlags.UntrustedRoot) != 0) return false;
        var anchor = chain.ChainElements[chain.ChainElements.Count - 1].Certificate.RawData;
        foreach (var root in roots)
            if (anchor.AsSpan().SequenceEqual(root.RawData)) return true;
        return false;
    }
#endif

    private static SdpException Interrupted(CancellationToken cancellationToken, string? token, Exception e, bool unknownOutcome = false)
    {
        return cancellationToken.IsCancellationRequested
            ? new SdpException(1009, "57014", "Operation cancelled", token is not null || unknownOutcome, token, inner: e)
            : new SdpException(1008, "HYT00", "Operation timed out", token is not null || unknownOutcome, token, inner: e);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.Close();
        _stream.Dispose();
        foreach (var root in _roots) root.Dispose();
    }
}
