"""Integration tests use local sockets only, with no private desktop data."""
import asyncio
import unittest
from relay import Relay


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

    async def client(self, role, room="a" * 32):
        reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
        self.clients.append(writer)
        writer.write(f"LUME1 {role} {room}\n".encode())
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

    async def test_rooms_are_isolated(self):
        reader, _ = await self.client("H", "b" * 32)
        self.assertEqual(await reader.readexactly(1), b"\x01")
        other, _ = await self.client("V", "c" * 32)
        self.assertEqual(await other.readexactly(1), b"\x02")
        self.assertEqual(len(self.relay.waiting), 1)


if __name__ == "__main__":
    unittest.main()
