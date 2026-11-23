// End-to-end test of the PR-SCTP patch: two SIPSorcery SCTP associations over an in-memory lossy link.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using SIPSorcery.Net;

class Link : SctpTransport
{
    public Link Peer;
    public SctpAssociation Assoc;
    public Func<SctpPacket, bool> Drop = _ => false;
    public double DelayMs = 5;
    public int InitWithForwardTsn = -1;
    readonly BlockingCollection<(byte[] buf, long due)> _q = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public Link() { new Thread(Pump) { IsBackground = true }.Start(); }

    public override void Send(string associationID, byte[] buffer, int offset, int length)
    {
        var copy = new byte[length];
        Buffer.BlockCopy(buffer, offset, copy, 0, length);
        var pkt = SctpPacket.Parse(copy, 0, copy.Length);
        if (Drop(pkt)) return;
        Peer._q.Add((copy, Clock.ElapsedTicks + (long)(DelayMs * Stopwatch.Frequency / 1000)));
    }

    void Pump()
    {
        foreach (var (buf, due) in _q.GetConsumingEnumerable())
        {
            while (Clock.ElapsedTicks < due) Thread.SpinWait(50);
            var pkt = SctpPacket.Parse(buf, 0, buf.Length);
            if (pkt.Chunks.FirstOrDefault(c => c.KnownType == SctpChunkType.INIT) is SctpInitChunk init)
            {
                InitWithForwardTsn = init.ForwardTsnSupported ? 1 : 0;
                GotInit(pkt, null);
            }
            else if (pkt.Chunks.Any(c => c.KnownType == SctpChunkType.COOKIE_ECHO))
            {
                Assoc.GotCookie(GetCookie(pkt));
                if (pkt.Chunks.Count() > 1) Assoc.OnPacketReceived(pkt);
            }
            else Assoc.OnPacketReceived(pkt);
        }
    }
}

static class P
{
    static int failed;
    static void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failed++; }

    static (Link a, Link b, SctpAssociation ca, SctpAssociation sb) Pair()
    {
        var a = new Link(); var b = new Link(); a.Peer = b; b.Peer = a;
        var ca = new SctpAssociation(a, null, 5000, 5000, 1200, 0);
        var sb = new SctpAssociation(b, null, 5000, 5000, 1200, 0);
        a.Assoc = ca; b.Assoc = sb;
        ca.Init();
        var sw = Stopwatch.StartNew();
        while ((ca.State != SctpAssociationState.Established || sb.State != SctpAssociationState.Established) && sw.ElapsedMilliseconds < 3000) Thread.Sleep(5);
        return (a, b, ca, sb);
    }

    static bool IsData(SctpPacket p, ushort stream) =>
        p.Chunks.Any(c => c is SctpDataChunk d && d.StreamID == stream && d.UserData != null && d.UserData.Length > 0);

    static (List<double> lat, int received, SctpAssociation ca, SctpAssociation sb, List<int> order) Run(bool unordered, int maxRetransmits, double loss, int n, int gapMs, ushort stream, int seed)
    {
        var (a, b, ca, sb) = Pair();
        var rnd = new Random(seed);
        a.Drop = p => IsData(p, stream) && rnd.NextDouble() < loss;
        var lat = new List<double>(); var order = new List<int>();
        var clock = Stopwatch.StartNew();
        sb.OnData += f =>
        {
            if (f.StreamID != stream) return;
            double sent = BitConverter.ToDouble(f.UserData, 4);
            lock (lat) { lat.Add(clock.Elapsed.TotalMilliseconds - sent); order.Add(BitConverter.ToInt32(f.UserData, 0)); }
        };
        for (int i = 0; i < n; i++)
        {
            var msg = new byte[40];
            BitConverter.GetBytes(i).CopyTo(msg, 0);
            BitConverter.GetBytes(clock.Elapsed.TotalMilliseconds).CopyTo(msg, 4);
            ca.SendData(stream, 53, msg, 0, msg.Length, unordered, maxRetransmits);
            Thread.Sleep(gapMs);
        }
        Thread.Sleep(1500);
        lock (lat) return (lat.ToList(), lat.Count, ca, sb, order.ToList());
    }

    static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    static double Pct(List<double> v, double p) { var s = v.OrderBy(x => x).ToList(); return s[(int)Math.Min(s.Count - 1, s.Count * p)]; }

    static int Main()
    {
        Console.WriteLine("PR-SCTP patch tests (in-memory link, 5 ms one-way delay)");

        var (la, lb, ca0, sb0) = Pair();
        Check(ca0.State == SctpAssociationState.Established && sb0.State == SctpAssociationState.Established, "association handshake completes");
        Check(lb.InitWithForwardTsn == 1, "INIT advertises Forward-TSN-Supported (0xC000)");

        var rel = Run(false, -1, 0.10, 400, 4, 1, 1);
        Check(rel.received == 400, $"reliable ordered: all 400 delivered ({rel.received})");
        Check(rel.order.SequenceEqual(Enumerable.Range(0, 400)), "reliable ordered: delivered in order");
        Console.WriteLine($"        reliable ordered latency p50 {Pct(rel.lat, .5):F1} ms, p99 {Pct(rel.lat, .99):F1} ms, max {rel.lat.Max():F1} ms");

        var un = Run(true, 0, 0.10, 400, 4, 2, 1);
        var sender = Field<SctpDataSender>(un.ca, "_dataSender");
        var receiver = Field<SctpDataReceiver>(un.sb, "_dataReceiver");
        uint nextTsn = sender.TSN;
        Console.WriteLine($"        unordered/no-rexmit latency p50 {Pct(un.lat, .5):F1} ms, p99 {Pct(un.lat, .99):F1} ms, max {un.lat.Max():F1} ms; delivered {un.received}/400, abandoned {sender.AbandonedCount}, FORWARD TSNs {sender.ForwardTsnSent}");
        Check(un.received >= 330 && un.received < 400, "unordered/no-rexmit: lost messages are dropped, not resent");
        Check(un.lat.Max() < 20, "unordered/no-rexmit: no message waits behind a lost one (max latency < 20 ms)");
        Check(sender.AbandonedCount > 0 && sender.ForwardTsnSent > 0, "sender abandons lost chunks and sends FORWARD TSN");
        Check(receiver.CumulativeAckTSN.HasValue && SctpDataReceiver.GetDistance(receiver.CumulativeAckTSN.Value, nextTsn - 1) <= 1,
            $"receiver's cumulative TSN caught up past every gap ({receiver.CumulativeAckTSN} vs last sent {nextTsn - 1})");
        Check(receiver.ForwardTSNCount == 0, "receiver holds no stale out-of-order state");

        var (ma, mb, mca, msb) = Pair();
        var rnd = new Random(7);
        ma.Drop = p => (IsData(p, 1) || IsData(p, 2)) && rnd.NextDouble() < 0.10;
        var got1 = new List<int>(); int got2 = 0;
        msb.OnData += f => { if (f.StreamID == 1) lock (got1) got1.Add(BitConverter.ToInt32(f.UserData, 0)); else if (f.StreamID == 2) Interlocked.Increment(ref got2); };
        for (int i = 0; i < 300; i++)
        {
            var m = BitConverter.GetBytes(i);
            mca.SendData(1, 53, m, 0, 4, false, -1);
            mca.SendData(2, 53, m, 0, 4, true, 0);
            Thread.Sleep(3);
        }
        Thread.Sleep(4000);
        lock (got1) Check(got1.SequenceEqual(Enumerable.Range(0, 300)), $"reliable stream still complete and ordered next to unreliable one ({got1.Count}/300)");
        Check(got2 > 240 && got2 < 300, $"unreliable stream delivered what arrived ({got2}/300)");

        var fwd = new SctpForwardTsnChunk(123456) { Streams = { (3, 9), (5, 65535) } };
        var buf = new byte[fwd.GetChunkLength(true)]; fwd.WriteTo(buf, 0);
        var back = (SctpForwardTsnChunk)SctpChunk.Parse(buf, 0);
        Check(buf[0] == 192 && back.NewCumulativeTSN == 123456 && back.Streams.SequenceEqual(new (ushort, ushort)[] { (3, 9), (5, 65535) }), "FORWARD TSN chunk round-trips");

        Console.WriteLine(failed == 0 ? "All PR-SCTP tests passed" : $"{failed} FAILED");
        return failed == 0 ? 0 : 1;
    }
}
