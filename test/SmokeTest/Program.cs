using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using RatcliffDefense.RadarDataExport;

internal static class Program
{
    private const int Port = 7099;
    private static int _failures;

    private static int Main()
    {
        var log = new ManualLogSource("smoke");
        var server = new TcpRadarServer("127.0.0.1", Port, log);

        Check("server starts", server.Start());
        Check("no clients initially", !server.HasClients);

        // Connect a client and let the acceptor register it.
        using var client = new TcpClient();
        client.Connect("127.0.0.1", Port);
        WaitUntil(() => server.HasClients, 2000);
        Check("client registered", server.HasClients);

        // Broadcast a snapshot and confirm the client receives it verbatim.
        const string snapshot = "{\"v\":1,\"hostile\":[],\"friendly\":[]}\n";
        server.Broadcast(snapshot);
        string received = ReadLine(client, 2000);
        Check("snapshot delivered", received == snapshot.TrimEnd('\n'),
            $"got: {received ?? "<null>"}");

        // A second client should get the latest snapshot immediately on connect.
        server.Broadcast("{\"v\":1,\"seq\":2}\n");
        using (var late = new TcpClient())
        {
            late.Connect("127.0.0.1", Port);
            string onConnect = ReadLine(late, 2000);
            Check("snapshot sent on connect", onConnect == "{\"v\":1,\"seq\":2}",
                $"got: {onConnect ?? "<null>"}");
        }
        WaitUntil(() => server.HasClients == false || true, 200); // settle

        // Drop the first client; next broadcast must prune it.
        client.Close();
        // The late client also closed (using block). Broadcast to trigger pruning.
        for (int i = 0; i < 5 && server.HasClients; i++)
        {
            server.Broadcast("{\"v\":1,\"prune\":true}\n");
            WaitUntil(() => !server.HasClients, 500);
        }
        Check("dead clients pruned", !server.HasClients);

        server.Stop();
        Check("stop is idempotent", Safe(() => server.Stop()));

        Console.WriteLine(_failures == 0 ? "\nALL CHECKS PASSED" : $"\n{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = null)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(ok || detail == null ? "" : " -- " + detail)}");
        if (!ok) _failures++;
    }

    private static bool Safe(Action a)
    {
        try { a(); return true; } catch { return false; }
    }

    private static void WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(10);
    }

    private static string ReadLine(TcpClient client, int timeoutMs)
    {
        try
        {
            client.ReceiveTimeout = timeoutMs;
            var buf = new byte[4096];
            var sb = new StringBuilder();
            NetworkStream s = client.GetStream();
            while (true)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] == (byte)'\n') return sb.ToString();
                    sb.Append((char)buf[i]);
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception e)
        {
            return "<read error: " + e.Message + ">";
        }
    }
}
