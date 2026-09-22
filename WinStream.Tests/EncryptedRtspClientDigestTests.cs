using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Tests;

/// <summary>
/// Drives <see cref="EncryptedRtspClient.SendAsync"/> over a real loopback socket
/// with a fake peer, so the Digest 401 retry/give-up rules are covered at the
/// client boundary instead of only through <c>RtspDigestAuthTests</c> helpers.
/// </summary>
public class EncryptedRtspClientDigestTests
{
    [Fact]
    public async Task Empty_username_challenge_succeeds_and_stays_sticky_on_the_next_request()
    {
        await using var harness = await Harness.StartAsync("secret");
        harness.Server.EnqueueChallengeThenAccept(realm: "AirPlay", nonce: "n1", username: "");

        var first = await harness.Client.SendAsync("GET", "/info", null, null);
        Assert.True(first.IsSuccessStatusCode);

        // Second request: no fresh 401 from the server, so success here only
        // happens if EncryptedRtspClient re-attached the sticky Authorization.
        harness.Server.EnqueueAcceptRequiringAuthorization(realm: "AirPlay", nonce: "n1", username: "");
        var second = await harness.Client.SendAsync("POST", "/feedback", null, null);
        Assert.True(second.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Wrong_password_throws_after_one_retry_instead_of_looping()
    {
        await using var harness = await Harness.StartAsync("wrong-guess");
        harness.Server.EnqueueChallengeThenChallenge(realm: "AirPlay", nonce: "n1");

        var exception = await Assert.ThrowsAsync<AirPlayPasswordRejectedException>(
            () => harness.Client.SendAsync("GET", "/info", null, null));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, harness.Server.RequestsHandled);
    }

    [Fact]
    public async Task A_later_401_with_a_rotated_nonce_is_retried_instead_of_short_circuited()
    {
        await using var harness = await Harness.StartAsync("secret");
        harness.Server.EnqueueChallengeThenAccept(realm: "AirPlay", nonce: "n1", username: "");
        var first = await harness.Client.SendAsync("SETUP", "/session", null, null);
        Assert.True(first.IsSuccessStatusCode);

        // The client now sticks nonce "n1" onto every request. The receiver
        // rotates to "n2"; the old alreadyAuthed short-circuit would have
        // returned this 401 straight to the caller instead of retrying.
        harness.Server.EnqueueChallengeThenAccept(realm: "AirPlay", nonce: "n2", username: "");
        var second = await harness.Client.SendAsync("RECORD", "/session", null, null);
        Assert.True(second.IsSuccessStatusCode);
    }

    [Fact]
    public async Task No_password_never_attempts_a_digest_retry()
    {
        await using var harness = await Harness.StartAsync(receiverPassword: null);
        harness.Server.EnqueueChallengeOnly(realm: "AirPlay", nonce: "n1");

        var response = await harness.Client.SendAsync("GET", "/info", null, null);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal(1, harness.Server.RequestsHandled);
    }

    /// <summary>Loopback client/server pair sharing symmetric ChaCha20 keys, no HKP handshake.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TcpClient _serverSocket;
        private readonly TcpClient _clientSocket;

        public EncryptedRtspClient Client { get; }

        public FakePeer Server { get; }

        private Harness(TcpClient serverSocket, TcpClient clientSocket, EncryptedRtspClient client, FakePeer server)
        {
            _serverSocket = serverSocket;
            _clientSocket = clientSocket;
            Client = client;
            Server = server;
        }

        public static async Task<Harness> StartAsync(string? receiverPassword)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync();

            var clientSocket = new TcpClient();
            await clientSocket.ConnectAsync(IPAddress.Loopback, port);
            var serverSocket = await acceptTask;
            listener.Stop();

            var toServer = RandomNumberGenerator.GetBytes(32);
            var toClient = RandomNumberGenerator.GetBytes(32);
            var clientCrypto = new RtspCryptoStream(clientSocket.GetStream(), toServer, toClient);
            var serverCrypto = new RtspCryptoStream(serverSocket.GetStream(), toClient, toServer);

            var client = new EncryptedRtspClient("127.0.0.1", port);
            client.InstallCryptoForTests(clientCrypto, receiverPassword);

            var server = new FakePeer(serverCrypto);
            return new Harness(serverSocket, clientSocket, client, server);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _serverSocket.Dispose();
            _clientSocket.Dispose();
        }
    }

    /// <summary>
    /// Fake AirPlay receiver. Each <c>Enqueue*</c> call schedules the next one or
    /// two request/response round trips on a private chain, so calls made in the
    /// test's Arrange step do not race the ones made after an earlier exchange.
    /// </summary>
    private sealed class FakePeer(RtspCryptoStream crypto)
    {
        private Task _serving = Task.CompletedTask;

        public int RequestsHandled { get; private set; }

        public void EnqueueChallengeOnly(string realm, string nonce) =>
            Serve(() => HandleAsync(_ => Challenge(realm, nonce)));

        public void EnqueueChallengeThenAccept(string realm, string nonce, string username) =>
            Serve(async () =>
            {
                await HandleAsync(_ => Challenge(realm, nonce)).ConfigureAwait(false);
                await HandleAsync(request =>
                {
                    AssertAuthorization(request, realm, nonce, username);
                    return Response("200 OK");
                }).ConfigureAwait(false);
            });

        public void EnqueueChallengeThenChallenge(string realm, string nonce) =>
            Serve(async () =>
            {
                await HandleAsync(_ => Challenge(realm, nonce)).ConfigureAwait(false);
                await HandleAsync(_ => Challenge(realm, nonce)).ConfigureAwait(false);
            });

        public void EnqueueAcceptRequiringAuthorization(string realm, string nonce, string username) =>
            Serve(() => HandleAsync(request =>
            {
                AssertAuthorization(request, realm, nonce, username);
                return Response("200 OK");
            }));

        private void Serve(Func<Task> run) =>
            _serving = _serving.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await run().ConfigureAwait(false);
                    }
                    catch
                    {
                        // A test that never sends the expected follow-up request
                        // (e.g. the no-password path) leaves this half-run; the
                        // socket disposal at teardown is enough to unblock it.
                    }
                },
                TaskScheduler.Default).Unwrap();

        private async Task HandleAsync(Func<string, string> respond)
        {
            var request = await ReadRequestAsync().ConfigureAwait(false);
            RequestsHandled++;
            var reply = respond(request);
            await crypto.WritePlaintextAsync(Encoding.ASCII.GetBytes(reply)).ConfigureAwait(false);
        }

        private async Task<string> ReadRequestAsync()
        {
            using var message = new MemoryStream();
            while (true)
            {
                var chunk = await crypto.ReadNextChunkAsync().ConfigureAwait(false);
                message.Write(chunk);
                var text = Encoding.ASCII.GetString(message.ToArray());
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    return text;
                }
            }
        }

        private static void AssertAuthorization(string request, string realm, string nonce, string username)
        {
            Assert.Contains($"realm=\"{realm}\"", request, StringComparison.Ordinal);
            Assert.Contains($"nonce=\"{nonce}\"", request, StringComparison.Ordinal);
            Assert.Contains($"username=\"{username}\"", request, StringComparison.Ordinal);
        }

        private static string Challenge(string realm, string nonce) =>
            "RTSP/1.0 401 Unauthorized\r\n" +
            $"WWW-Authenticate: Digest realm=\"{realm}\", nonce=\"{nonce}\"\r\n" +
            "Content-Length: 0\r\n\r\n";

        private static string Response(string status) =>
            $"RTSP/1.0 {status}\r\nContent-Length: 0\r\n\r\n";
    }
}
