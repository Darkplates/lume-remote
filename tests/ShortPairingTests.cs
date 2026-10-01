using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LumeRemote;

// Short-code pairing. The handshake check runs in every suite (no network); the
// public-broker round trip runs only with --signal because it contacts 0.peerjs.com.
static partial class Tests
{
    static string SamplePairingCode()
    {
        return new PairingCode { host = SignalBroker.NewId(), id = Guid.NewGuid().ToString("N"), key = Security.Token(32), name = "Pairing fixture", expires = DateTime.UtcNow.AddMinutes(15).Ticks }.ToString();
    }
    static void ShortPairingHandshake()
    {
        string code = ShortPairing.NewCode();
        Check(code.Length == 8 && ShortPairing.Normalize(code) == code && ShortPairing.Normalize(ShortPairing.Format(code)) == code && ShortPairing.Normalize(code.Substring(0, 4) + "-" + code.Substring(4)) == code, "Short codes do not round-trip through typing formats.");
        Check(ShortPairing.Normalize("1234567") == null && ShortPairing.Normalize("123456789") == null && ShortPairing.Normalize("1234x678") == null && ShortPairing.Normalize("lume-pair://abc") == null, "Invalid short codes were accepted.");
        Check(ShortPairing.HostId(code) == ShortPairing.HostId(code) && ShortPairing.ValidPeer(ShortPairing.HostId(code)) && Invitation.IsHex(ShortPairing.Route(code), 32), "Short-code rendezvous identities are invalid.");
        Check(ShortPairing.HostId("00000000") != ShortPairing.HostId("00000001"), "Different short codes share a rendezvous.");
        string route = ShortPairing.Route(code);
        using (PairingHandshake offer = new PairingHandshake()) using (PairingHandshake join = new PairingHandshake())
        {
            byte[] commitment = join.Commitment();
            Check(PairingHandshake.Opens(commitment, join.PublicKey, join.Nonce), "A valid commitment did not open.");
            Check(!PairingHandshake.Opens(commitment, offer.PublicKey, join.Nonce) && !PairingHandshake.Opens(commitment, join.PublicKey, offer.Nonce), "A commitment opened with different values.");
            offer.Complete(route, offer.PublicKey, offer.Nonce, join.PublicKey, join.Nonce, true);
            join.Complete(route, offer.PublicKey, offer.Nonce, join.PublicKey, join.Nonce, false);
            Check(offer.Sas == join.Sas && offer.Sas.Length == 7 && offer.SessionKey == join.SessionKey, "Both PCs did not derive the same comparison number and key.");
            string sender = SignalBroker.NewId(), receiver = SignalBroker.NewId(), pairing = SamplePairingCode();
            SignalEnvelope sealedCode = SignalCrypto.Seal(offer.SessionKey, sender, receiver, route, Guid.NewGuid().ToString("N"), "code", new SignalBody { code = pairing });
            Check(SignalCrypto.Open(join.SessionKey, sender, receiver, sealedCode).code == pairing, "The pairing code changed in transit.");
            Reject(delegate { SignalCrypto.Open(ShortPairing.EnvelopeKey(code), sender, receiver, sealedCode); });
            // A relay that substitutes its own key ends with a different number on one side.
            using (PairingHandshake relay = new PairingHandshake())
            using (PairingHandshake relayedJoin = new PairingHandshake())
            {
                relayedJoin.Complete(route, relay.PublicKey, relay.Nonce, relayedJoin.PublicKey, relayedJoin.Nonce, false);
                relay.Complete(route, relay.PublicKey, relay.Nonce, relayedJoin.PublicKey, relayedJoin.Nonce, true);
                Check(relayedJoin.SessionKey != offer.SessionKey && relay.Sas == relayedJoin.Sas, "Key substitution was not isolated to the relayed session.");
            }
            byte[] invalid = new byte[PairingHandshake.PublicKeyLength]; for (int i = 0; i < invalid.Length; i++) invalid[i] = 0xFF;
            using (PairingHandshake victim = new PairingHandshake()) Reject(delegate { victim.Complete(route, invalid, offer.Nonce, victim.PublicKey, victim.Nonce, false); });
        }
    }
    static void InvitationLinkRoundTrip()
    {
        Invitation sample = Sample();
        PeerSignal offer = PeerSignal.Offer(sample, SyntheticSdp("hostufrag"));
        foreach (string invitation in new[] { offer.ToString(), sample.ToString() })
        {
            string link = InvitationLinks.Link(invitation);
            Check(link.StartsWith(InvitationLinks.OpenPage + "#", StringComparison.Ordinal) && InvitationLinks.Unwrap(link) == invitation, "A shared link did not return the exact invitation.");
            string launch = "lume-open:" + Uri.EscapeDataString(invitation);
            Check(InvitationLinks.IsLaunch(launch) && InvitationLinks.Unwrap(launch) == invitation && InvitationLinks.Unwrap(launch + "/") == invitation, "A link launch did not return the exact invitation.");
            Check(InvitationLinks.Unwrap("  " + invitation + " ") == invitation, "A pasted invitation was changed.");
        }
        Check(!InvitationLinks.IsLaunch("lume-p2p://x") && !InvitationLinks.IsLaunch("https://example.com"), "A non-link argument was treated as a link launch.");
        Reject(delegate { InvitationLinks.Unwrap("lume-open:" + Uri.EscapeDataString("lume-pair://not-a-guest-invitation")); });
        Reject(delegate { InvitationLinks.Unwrap(InvitationLinks.OpenPage + "#https%3A%2F%2Fexample.com"); });
        Reject(delegate { InvitationLinks.Unwrap(new string('a', 200001)); });
    }
    static void ShortPairingPublic()
    {
        string pairing = SamplePairingCode(), offerNumber = null, joinNumber = null, claimed = null;
        using (ManualResetEvent ready = new ManualResetEvent(false))
        {
            ShortPairingOffer offer = ShortPairingOffer.Start(pairing, "Sharing fixture").GetAwaiter().GetResult();
            try
            {
                offer.Ready += delegate(string number, string name) { offerNumber = number; claimed = name; ready.Set(); offer.Confirm(); };
                string received;
                using (CancellationTokenSource limit = new CancellationTokenSource(60000))
                    received = ShortPairing.Join(offer.Code, "Joining fixture", delegate(string number, string name) { joinNumber = number; return Task.FromResult(name == "Sharing fixture"); }, delegate { }, limit.Token).GetAwaiter().GetResult();
                Check(ready.WaitOne(5000) && offerNumber != null && offerNumber == joinNumber && claimed == "Joining fixture", "Both PCs did not show the same number and name.");
                Check(received == pairing, "The joining PC did not receive the exact pairing code.");
            }
            finally { offer.Dispose(); }
        }
        // Declining on the joining side shares nothing.
        ShortPairingOffer declined = ShortPairingOffer.Start(SamplePairingCode(), "Sharing fixture").GetAwaiter().GetResult();
        try
        {
            string reported = null; using (ManualResetEvent failed = new ManualResetEvent(false))
            {
                declined.Failed += delegate(string message) { reported = message; failed.Set(); };
                bool cancelled = false;
                try { using (CancellationTokenSource limit = new CancellationTokenSource(60000)) ShortPairing.Join(declined.Code, "Joining fixture", delegate { return Task.FromResult(false); }, delegate { }, limit.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && failed.WaitOne(15000) && reported.Contains("do not match"), "A declined comparison was not reported to both PCs.");
            }
        }
        finally { declined.Dispose(); }
        // The sharing owner seeing different numbers stops the joining PC at once.
        ShortPairingOffer refused = ShortPairingOffer.Start(SamplePairingCode(), "Sharing fixture").GetAwaiter().GetResult();
        try
        {
            refused.Ready += delegate { refused.Reject(); };
            Stopwatch clock = Stopwatch.StartNew(); bool stopped = false;
            try { using (CancellationTokenSource limit = new CancellationTokenSource(60000)) ShortPairing.Join(refused.Code, "Joining fixture", delegate { return Task.FromResult(true); }, delegate { }, limit.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException error) { stopped = error.Message.Contains("did not match"); }
            Check(stopped && clock.ElapsedMilliseconds < 30000, "A refusal on the sharing PC did not stop the joining PC promptly.");
        }
        finally { refused.Dispose(); }
        bool missing = false;
        try { using (CancellationTokenSource limit = new CancellationTokenSource(60000)) ShortPairing.Join(ShortPairing.NewCode(), "Joining fixture", delegate { return Task.FromResult(true); }, delegate { }, limit.Token).GetAwaiter().GetResult(); }
        catch (Exception error) { missing = error is System.IO.IOException || error is TimeoutException; }
        Check(missing, "A code that no PC is showing did not fail clearly.");
        Console.WriteLine("SHORT_PAIRING: matching numbers delivered the exact code through the public broker; a declined comparison and an unknown code shared nothing. No code, key or identity logged.");
    }
}
