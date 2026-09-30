using System;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

// Automatic return of a guest's P2P reply. The offline check runs in every suite;
// the public-broker check runs only with --signal because it contacts 0.peerjs.com.
static partial class Tests
{
    static string SyntheticSdp(string tag)
    {
        return "v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n" +
            "a=ice-ufrag:" + tag + "\r\na=ice-pwd:" + tag + "passwordpasswordpass\r\na=fingerprint:sha-256 " + String.Join(":", System.Linq.Enumerable.Repeat("AB", 32)) + "\r\n";
    }
    static void GuestRendezvousIdentities()
    {
        Invitation first = Sample(), second = Sample();
        string id = GuestRendezvous.HostId(first), route = GuestRendezvous.Route(first);
        using (new SignalBroker(id)) { }
        Check(id == GuestRendezvous.HostId(first) && route == GuestRendezvous.Route(first), "Rendezvous identities are not deterministic.");
        Check(id != GuestRendezvous.HostId(second) && route != GuestRendezvous.Route(second), "Different invitations share a rendezvous identity.");
        Check(Invitation.IsHex(route, 32) && id.Substring(5) != route, "Invalid or reused rendezvous route.");
        Check(id.IndexOf(first.Secret, StringComparison.Ordinal) < 0 && route.IndexOf(first.Fingerprint, StringComparison.OrdinalIgnoreCase) < 0, "A rendezvous identity exposes invitation material.");
        PeerSignal offer = PeerSignal.Offer(first, SyntheticSdp("hostufrag")), reply = offer.Reply(SyntheticSdp("viewufrag"));
        string viewer = SignalBroker.NewId(), request = Guid.NewGuid().ToString("N");
        SignalEnvelope sealedReply = SignalCrypto.Seal(first.Secret, viewer, id, route, request, "answer", new SignalBody { code = reply.ToString() });
        PeerSignal opened = PeerSignal.Parse(SignalCrypto.Open(first.Secret, viewer, id, sealedReply).code); offer.VerifyReply(opened);
        Reject(delegate { SignalCrypto.Open(second.Secret, viewer, id, sealedReply); });
        Reject(delegate { SignalCrypto.Open(first.Secret, SignalBroker.NewId(), id, sealedReply); });
        PeerSignal foreign = PeerSignal.Offer(second, SyntheticSdp("otherufrag")).Reply(SyntheticSdp("viewufrag"));
        Reject(delegate { offer.VerifyReply(foreign); });
    }
    static void GuestRendezvousPublic()
    {
        Invitation session = Sample();
        PeerSignal offer = PeerSignal.Offer(session, SyntheticSdp("hostufrag")), reply = offer.Reply(SyntheticSdp("viewufrag"));
        PeerSignal other = PeerSignal.Offer(Sample(), SyntheticSdp("otherufrag"));
        TaskCompletionSource<PeerSignal> received = new TaskCompletionSource<PeerSignal>(); int count = 0;
        SignalBroker host = GuestRendezvous.Listen(offer, delegate(PeerSignal value) { Interlocked.Increment(ref count); received.TrySetResult(value); }).GetAwaiter().GetResult();
        try
        {
            // A reply for another invitation, sent to this rendezvous, must be ignored and never acknowledged.
            using (SignalBroker intruder = new SignalBroker(SignalBroker.NewId()))
            {
                intruder.Start(Security.Token(32)).GetAwaiter().GetResult();
                intruder.Send(host.Id, SignalCrypto.Seal(other.Session.Secret, intruder.Id, host.Id, GuestRendezvous.Route(session), Guid.NewGuid().ToString("N"), "answer",
                    new SignalBody { code = other.Reply(SyntheticSdp("viewufrag")).ToString() })).GetAwaiter().GetResult();
            }
            Check(GuestRendezvous.Deliver(offer, reply, CancellationToken.None).GetAwaiter().GetResult(), "The sharing side did not confirm the automatic reply.");
            Check(received.Task.Wait(5000) && received.Task.Result.Sdp == reply.Sdp && received.Task.Result.Signature == reply.Signature, "The automatic reply changed in transit.");
            Check(!GuestRendezvous.Deliver(offer, reply, CancellationToken.None).GetAwaiter().GetResult(), "A second reply was accepted for a single invitation.");
            Check(Volatile.Read(ref count) == 1, "The reply handler ran more than once.");
            PeerSignal absent = PeerSignal.Offer(Sample(), SyntheticSdp("hostufrag"));
            Check(!GuestRendezvous.Deliver(absent, absent.Reply(SyntheticSdp("viewufrag")), CancellationToken.None).GetAwaiter().GetResult(), "Delivery claimed success without a listening host.");
            Console.WriteLine("GUEST_RENDEZVOUS: encrypted reply confirmed through the public broker; foreign, repeated and unheard replies were not confirmed. No code, key or identity logged.");
        }
        finally { host.Dispose(); }
    }
}
