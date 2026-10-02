"""Integration tests use local sockets only, with no private desktop data."""
import asyncio
import unittest
from relay import Relay, source_key


class RelayTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.relay = Relay(32)
        self.server = await asyncio.start_server(self.relay.handle, "127.0.0.1", 0, limit=128)
        self.port = self.server.sockets[0].getsockname()[1]
        self.clients = []

    async def asyncTearDown(self):
        for writer in self.clients:
            writer.close()
            await writer.wait_closed()
        self.server.close()
        await self.server.wait_closed()
        await self.relay.close()

    async def client(self, role, room="a" * 32, token=None):
        reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
        self.clients.append(writer)
        suffix = f" {token}" if token else ""
        writer.write(f"LUME1 {role} {room}{suffix}\n".encode())
        await writer.drain()
        return reader, writer

    async def test_full_duplex_and_cleanup(self):
        host, hw = await self.client("H")
        self.assertEqual(await host.readexactly(1), b"\x01")
        viewer, vw = await self.client("V")
        self.assertEqual(await viewer.readexactly(1), b"\x01")
        self.assertEqual(await host.readexactly(1), b"\x01")
        payload = bytes(range(256)) * 2048
        vw.write(payload)
        await vw.drain()
        self.assertEqual(await host.readexactly(len(payload)), payload)
        hw.write(payload[::-1])
        await hw.drain()
        self.assertEqual(await viewer.readexactly(len(payload)), payload[::-1])
        vw.close()
        self.assertEqual(await asyncio.wait_for(host.read(1), 2), b"")
        self.assertEqual(len(self.relay.waiting), 0)

    async def test_per_address_cap(self):
        self.relay.max_per_address = 2
        first, _ = await self.client("H", "b" * 32)
        second, _ = await self.client("H", "c" * 32)
        self.assertEqual(await first.readexactly(1), b"\x01")
        self.assertEqual(await second.readexactly(1), b"\x01")
        third, tw = await self.client("H", "d" * 32)
        self.assertEqual(await third.readexactly(1), b"\x03")
        self.assertEqual(self.relay.per_address.get("127.0.0.1"), 2)
        # The relay closes a refused socket with its header unread, so the peer may reset.
        self.clients.remove(tw)
        tw.close()

    async def test_missing_host(self):
        reader, _ = await self.client("V")
        self.assertEqual(await reader.readexactly(1), b"\x02")

    async def test_duplicate_host_is_rejected(self):
        first, _ = await self.client("H")
        self.assertEqual(await first.readexactly(1), b"\x01")
        second, _ = await self.client("H")
        self.assertEqual(await second.readexactly(1), b"\x03")

    async def test_bad_header(self):
        reader, _ = await self.client("X", "bad")
        self.assertEqual(await reader.readexactly(1), b"\x03")

    async def test_long_header_is_closed(self):
        reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
        self.clients.append(writer)
        writer.write(b"X" * 1024 + b"\n")
        await writer.drain()
        self.assertEqual(await asyncio.wait_for(reader.read(1), 2), b"")

    async def test_host_disconnect_removes_room(self):
        reader, writer = await self.client("H")
        self.assertEqual(await reader.readexactly(1), b"\x01")
        writer.close()
        await writer.wait_closed()
        for _ in range(20):
            if not self.relay.waiting:
                break
            await asyncio.sleep(0.01)
        self.assertEqual(len(self.relay.waiting), 0)

    async def paired(self, room="a" * 32, token=None):
        host, hw = await self.client("H", room, token)
        self.assertEqual(await host.readexactly(1), b"\x01")
        viewer, vw = await self.client("V", room)
        self.assertEqual(await viewer.readexactly(1), b"\x01")
        self.assertEqual(await host.readexactly(1), b"\x01")
        return host, hw, viewer, vw

    async def wait_for_cleanup(self):
        for _ in range(200):
            if self.relay.connections == 0 and not self.relay.waiting:
                return
            await asyncio.sleep(0.01)
        self.fail("Relay did not release its connections")

    async def test_idle_pipe_is_closed(self):
        self.relay.idle_timeout = 0.3
        host, _, viewer, _ = await self.paired()
        self.assertEqual(await asyncio.wait_for(host.read(1), 3), b"")
        self.assertEqual(await asyncio.wait_for(viewer.read(1), 3), b"")
        await self.wait_for_cleanup()

    async def test_one_way_traffic_keeps_pipe_open(self):
        # Idle means no bytes in EITHER direction; a silent reverse direction is healthy.
        self.relay.idle_timeout = 0.4
        host, _, _, vw = await self.paired()
        for n in range(12):
            vw.write(bytes([n]))
            await vw.drain()
            self.assertEqual(await asyncio.wait_for(host.readexactly(1), 2), bytes([n]))
            await asyncio.sleep(0.1)
        self.assertEqual(self.relay.connections, 2)
        self.assertEqual(await asyncio.wait_for(host.read(1), 3), b"")
        await self.wait_for_cleanup()

    async def test_zero_idle_timeout_disables_it(self):
        self.relay.idle_timeout = 0
        host, hw, viewer, _ = await self.paired()
        await asyncio.sleep(0.3)
        hw.write(b"late")
        await hw.drain()
        self.assertEqual(await asyncio.wait_for(viewer.readexactly(4), 2), b"late")

    async def test_unpaired_host_wait_is_bounded(self):
        self.relay.host_wait_timeout = 0.2
        host, _ = await self.client("H")
        self.assertEqual(await host.readexactly(1), b"\x01")
        self.assertEqual(await asyncio.wait_for(host.read(1), 3), b"")
        await self.wait_for_cleanup()
        viewer, _ = await self.client("V")
        self.assertEqual(await viewer.readexactly(1), b"\x02")

    async def test_zero_host_wait_is_unlimited(self):
        self.relay.host_wait_timeout = 0
        host, _ = await self.client("H")
        self.assertEqual(await host.readexactly(1), b"\x01")
        await asyncio.sleep(0.3)
        self.assertEqual(len(self.relay.waiting), 1)
        viewer, _ = await self.client("V")
        self.assertEqual(await viewer.readexactly(1), b"\x01")
        self.assertEqual(await host.readexactly(1), b"\x01")

    async def test_rooms_are_isolated(self):
        reader, _ = await self.client("H", "b" * 32)
        self.assertEqual(await reader.readexactly(1), b"\x01")
        other, _ = await self.client("V", "c" * 32)
        self.assertEqual(await other.readexactly(1), b"\x02")
        self.assertEqual(len(self.relay.waiting), 1)

    async def test_waiting_hosts_per_address_are_capped(self):
        self.relay.max_waiting_per_address = 2
        for room in ("b" * 32, "c" * 32):
            reader, _ = await self.client("H", room)
            self.assertEqual(await reader.readexactly(1), b"\x01")
        third, _ = await self.client("H", "d" * 32)
        self.assertEqual(await third.readexactly(1), b"\x03")
        self.assertEqual(len(self.relay.waiting), 2)
        # A paired room no longer counts as waiting, so the source may register again.
        viewer, _ = await self.client("V", "b" * 32)
        self.assertEqual(await viewer.readexactly(1), b"\x01")
        fourth, _ = await self.client("H", "e" * 32)
        self.assertEqual(await fourth.readexactly(1), b"\x01")

    async def test_operator_token_is_required_for_hosts_only(self):
        token = "operator-token-0123456789"
        self.relay = Relay(32, token=token)
        self.server.close()
        await self.server.wait_closed()
        self.server = await asyncio.start_server(self.relay.handle, "127.0.0.1", 0, limit=128)
        self.port = self.server.sockets[0].getsockname()[1]
        for wrong in (None, "operator-token-0123456780"):
            reader, _ = await self.client("H", "b" * 32, wrong)
            self.assertEqual(await reader.readexactly(1), b"\x03")
        self.assertEqual(len(self.relay.waiting), 0)
        await self.paired("c" * 32, token)

    async def test_token_suffix_is_accepted_without_a_configured_token(self):
        await self.paired("b" * 32, "any-client-token-value")

    def test_token_configuration_is_validated(self):
        for bad in ("short", "x" * 65, "has space in it here", "bad\ntoken-0123456789"):
            with self.assertRaises(ValueError):
                Relay(token=bad)
        self.assertIsNone(Relay().token)

    def test_ipv6_sources_are_grouped_by_64(self):
        self.assertEqual(source_key("2001:db8:1:2:aaaa::1"), source_key("2001:db8:1:2:bbbb::9"))
        self.assertNotEqual(source_key("2001:db8:1:2::1"), source_key("2001:db8:1:3::1"))
        self.assertEqual(source_key("::ffff:192.0.2.7"), "192.0.2.7")
        self.assertEqual(source_key("fe80::1%eth0"), source_key("fe80::2"))
        self.assertNotEqual(source_key("192.0.2.7"), source_key("192.0.2.8"))
        self.assertIsNone(source_key(None))

    def test_default_host_wait_is_bounded(self):
        self.assertEqual(Relay().host_wait_timeout, 600)


if __name__ == "__main__":
    unittest.main()
